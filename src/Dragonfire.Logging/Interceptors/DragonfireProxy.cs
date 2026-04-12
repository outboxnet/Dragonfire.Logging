using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Dragonfire.Logging.Attributes;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Models;
using Dragonfire.Logging.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Interceptors
{
    // ────────────────────────────────────────────────────────────────────────────
    // Non-generic factory — called from DecorateLoggableServices with a runtime
    // Type.  Builds a closed DragonfireProxy<T> and caches the reflection work so
    // repeated DI resolutions are cheap.
    // ────────────────────────────────────────────────────────────────────────────
    internal static class DragonfireProxyFactory
    {
        private static readonly ConcurrentDictionary<Type, Func<object, IServiceProvider, object>>
            s_cache = new();

        /// <summary>
        /// Creates a <see cref="DragonfireProxy{T}"/> proxy that wraps
        /// <paramref name="inner"/> for <paramref name="serviceType"/>.
        /// </summary>
        internal static object Create(Type serviceType, object inner, IServiceProvider provider)
            => s_cache.GetOrAdd(serviceType, BuildFactory)(inner, provider);

        private static Func<object, IServiceProvider, object> BuildFactory(Type t)
        {
            // DragonfireProxy<T> is the closed generic — Wrap is the internal factory method.
            var wrapMethod = typeof(DragonfireProxy<>)
                .MakeGenericType(t)
                .GetMethod("Wrap", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    $"Could not locate Wrap method on DragonfireProxy<{t.Name}>.");

            return (inner, provider) =>
                wrapMethod.Invoke(null, new object[] { inner, provider })
                ?? throw new InvalidOperationException("Wrap returned null.");
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Generic DispatchProxy — one concrete type per service interface T.
    //
    // How it works:
    //   • DispatchProxy.Create<T, DragonfireProxy<T>>() instantiates the proxy
    //     without going through a public constructor (DispatchProxy's own plumbing).
    //   • Every call on the proxy interface routes to Invoke().
    //   • Invoke dispatches to InterceptSync, InterceptAsync, or
    //     InterceptAsyncWithResult<TResult> based on the return type.
    //   • TargetInvocationException (thrown by MethodInfo.Invoke when the target
    //     method throws) is always unwrapped so callers see the original exception.
    //
    // [LogProperty] support:
    //   • Parameters decorated with [LogProperty] are extracted directly from args.
    //   • DTO properties decorated with [LogProperty] are extracted via reflection
    //     on each argument object and on the return value.
    //   • All promoted values land in LogEntry.NamedProperties and are emitted as
    //     individual scope entries (no Dragonfire.* prefix — clean KQL keys).
    //
    // Depth control:
    //   • ResolveMaxDepth() picks the effective depth from [LogAttribute].MaxDepth
    //     (when >= 0) or DragonfireLoggingOptions.DefaultMaxDepth.
    //   • The resolved depth is forwarded to every FilterData() call.
    // ────────────────────────────────────────────────────────────────────────────
    internal class DragonfireProxy<T> : DispatchProxy where T : class
    {
        // Fields are set by Wrap() immediately after DispatchProxy.Create.
        private T                         _inner          = null!;
        private IDragonfireLoggingService  _loggingService = null!;
        private ILogFilterService          _filterService  = null!;
        private DragonfireLoggingOptions   _options        = null!;

        // Cache: Task result-type → closed generic InterceptAsyncWithResult<TResult>.
        // Static per closed generic DragonfireProxy<T>, so shared across all proxy
        // instances for the same service interface.
        private static readonly MethodInfo s_interceptAsyncWithResultDef =
            typeof(DragonfireProxy<T>)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "InterceptAsyncWithResult" && m.IsGenericMethodDefinition);

        private static readonly ConcurrentDictionary<Type, MethodInfo> s_closedMethodCache = new();

        // ── Factory ───────────────────────────────────────────────────────────

        internal static T Wrap(object inner, IServiceProvider provider)
        {
            // DispatchProxy.Create allocates the proxy via a protected constructor;
            // we then inject dependencies through the instance fields.
            var proxy = Create<T, DragonfireProxy<T>>();
            var p     = (DragonfireProxy<T>)(object)proxy;

            p._inner          = (T)inner;
            p._loggingService = provider.GetRequiredService<IDragonfireLoggingService>();
            p._filterService  = provider.GetRequiredService<ILogFilterService>();
            p._options        = provider.GetRequiredService<DragonfireLoggingOptions>();

            return proxy;
        }

        // ── DispatchProxy entry point ─────────────────────────────────────────

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;

            var returnType = targetMethod.ReturnType;

            // Task (void async)
            if (returnType == typeof(Task))
                return InterceptAsync(targetMethod, args);

            // Task<TResult>
            if (returnType.IsGenericType &&
                returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var closed = s_closedMethodCache.GetOrAdd(resultType,
                    t => s_interceptAsyncWithResultDef.MakeGenericMethod(t));
                // Invoke returns Task<TResult> — the runtime awaits it correctly.
                return closed.Invoke(this, new object?[] { targetMethod, args });
            }

            // Synchronous (including void)
            return InterceptSync(targetMethod, args);
        }

        // ── Intercept implementations ────────────────────────────────────────

        private object? InterceptSync(MethodInfo method, object?[]? args)
        {
            var attr     = ResolveAttribute(method);
            var maxDepth = ResolveMaxDepth(attr);
            var entry    = BuildEntry(method, args, attr, maxDepth);
            var sw       = Stopwatch.StartNew();

            try
            {
                var result = CallTarget(method, args);

                if (result is not null && method.ReturnType != typeof(void))
                {
                    entry.MethodResult = _filterService.FilterData(
                        result, Excl(attr), Incl(attr), MaxLen(attr), maxDepth);

                    ExtractResultNamedProperties(entry, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                MarkError(entry, ex);
                throw;
            }
            finally
            {
                sw.Stop();
                entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                // ILogger.Log is synchronous — no blocking concern here.
                _loggingService.Log(entry);
            }
        }

        private async Task InterceptAsync(MethodInfo method, object?[]? args)
        {
            var attr     = ResolveAttribute(method);
            var maxDepth = ResolveMaxDepth(attr);
            var entry    = BuildEntry(method, args, attr, maxDepth);
            var sw       = Stopwatch.StartNew();

            try
            {
                await ((Task)CallTarget(method, args)!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MarkError(entry, ex);
                throw;
            }
            finally
            {
                sw.Stop();
                entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                await _loggingService.LogAsync(entry).ConfigureAwait(false);
            }
        }

        // Called via reflection for Task<TResult> methods.
        private async Task<TResult> InterceptAsyncWithResult<TResult>(MethodInfo method, object?[]? args)
        {
            var attr     = ResolveAttribute(method);
            var maxDepth = ResolveMaxDepth(attr);
            var entry    = BuildEntry(method, args, attr, maxDepth);
            var sw       = Stopwatch.StartNew();

            // default! is safe: if an exception is thrown, return is never reached.
            TResult result = default!;

            try
            {
                result = await ((Task<TResult>)CallTarget(method, args)!).ConfigureAwait(false);

                // Capture result and named properties on the success path,
                // BEFORE the finally block logs the entry.
                entry.MethodResult = _filterService.FilterData(
                    result, Excl(attr), Incl(attr), MaxLen(attr), maxDepth);

                ExtractResultNamedProperties(entry, result);
            }
            catch (Exception ex)
            {
                MarkError(entry, ex);
                throw;
            }
            finally
            {
                sw.Stop();
                entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                await _loggingService.LogAsync(entry).ConfigureAwait(false);
            }

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Calls the real method on <see cref="_inner"/> and unwraps any
        /// <see cref="TargetInvocationException"/> so callers always see the
        /// original exception with its original stack trace intact.
        /// </summary>
        private object? CallTarget(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // Rethrow the original exception without losing the stack trace.
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                return null; // unreachable — satisfies the compiler
            }
        }

        private LogEntry BuildEntry(MethodInfo method, object?[]? args, LogAttribute? attr, int maxDepth)
        {
            var entry = new LogEntry
            {
                ServiceName   = typeof(T).Name,
                MethodName    = method.Name,
                Level         = attr?.Level ?? _options.DefaultLogLevel,
                CustomContext = attr?.CustomContext,
            };

            if (args is { Length: > 0 })
            {
                entry.MethodArguments = _filterService.FilterData(
                    args, Excl(attr), Incl(attr), MaxLen(attr), maxDepth);

                // Extract [LogProperty] from parameter declarations and argument objects.
                ExtractArgumentNamedProperties(entry, method, args);
            }

            return entry;
        }

        private void MarkError(LogEntry entry, Exception ex)
        {
            entry.IsError      = true;
            entry.Level        = LogLevel.Error;
            entry.ErrorMessage = ex.Message;
            if (_options.IncludeStackTraceOnError)
                entry.StackTrace = ex.StackTrace;
        }

        // ── [LogProperty] extraction ─────────────────────────────────────────

        /// <summary>
        /// Collects named properties from:
        /// <list type="number">
        ///   <item>Method parameters decorated with <c>[LogProperty]</c> — value taken directly from <paramref name="args"/>.</item>
        ///   <item>Properties on each argument object decorated with <c>[LogProperty]</c>.</item>
        /// </list>
        /// Results are merged into <see cref="LogEntry.NamedProperties"/>.
        /// </summary>
        private void ExtractArgumentNamedProperties(LogEntry entry, MethodInfo method, object?[]? args)
        {
            if (args is null || args.Length == 0) return;

            var named = entry.NamedProperties
                        ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // 1. Parameter-level [LogProperty]
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length && i < args.Length; i++)
            {
                var logProp = parameters[i].GetCustomAttribute<LogPropertyAttribute>();
                if (logProp is null) continue;

                var key = logProp.Name ?? parameters[i].Name ?? $"param{i}";
                named.TryAdd(key, args[i]);
            }

            // 2. Property-level [LogProperty] on each argument object
            foreach (var arg in args)
            {
                foreach (var (k, v) in _filterService.ExtractNamedProperties(arg))
                    named.TryAdd(k, v);
            }

            if (named.Count > 0)
                entry.NamedProperties = named;
        }

        /// <summary>
        /// Merges <c>[LogProperty]</c>-decorated properties from the method return value
        /// into <see cref="LogEntry.NamedProperties"/>. Called only on the success path.
        /// </summary>
        private void ExtractResultNamedProperties(LogEntry entry, object? result)
        {
            if (result is null) return;

            var extracted = _filterService.ExtractNamedProperties(result);
            if (extracted.Count == 0) return;

            var named = entry.NamedProperties
                        ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (k, v) in extracted)
                named.TryAdd(k, v);

            entry.NamedProperties = named;
        }

        // ── Attribute / option resolution ────────────────────────────────────

        private LogAttribute? ResolveAttribute(MethodInfo method)
            => (LogAttribute?)method.GetCustomAttributes(typeof(LogAttribute), true).FirstOrDefault()
               ?? (LogAttribute?)typeof(T).GetCustomAttributes(typeof(LogAttribute), true).FirstOrDefault();

        /// <summary>
        /// Resolves the effective max depth: attribute-level takes precedence when
        /// it is non-negative; otherwise falls back to the global default.
        /// </summary>
        private int ResolveMaxDepth(LogAttribute? attr)
            => (attr is not null && attr.MaxDepth >= 0)
                ? attr.MaxDepth
                : _options.DefaultMaxDepth;

        private static string[] Excl(LogAttribute? a)  => a?.ExcludeProperties ?? Array.Empty<string>();
        private static string[] Incl(LogAttribute? a)  => a?.IncludeProperties  ?? Array.Empty<string>();
        private        int      MaxLen(LogAttribute? a) => a?.MaxContentLength   ?? _options.DefaultMaxContentLength;
    }
}
