using System;
using System.Collections.Concurrent;
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
    // ────────────────────────────────────────────────────────────────────────────
    internal class DragonfireProxy<T> : DispatchProxy where T : class
    {
        // Fields are set by Wrap() immediately after DispatchProxy.Create.
        private T                        _inner          = null!;
        private IDragonfireLoggingService _loggingService = null!;
        private ILogFilterService         _filterService  = null!;
        private DragonfireLoggingOptions  _options        = null!;

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
            var attr  = ResolveAttribute(method);
            var entry = BuildEntry(method, args, attr);
            var sw    = Stopwatch.StartNew();
            object? result = null;

            try
            {
                result = CallTarget(method, args);

                if (result is not null && method.ReturnType != typeof(void))
                    entry.MethodResult = _filterService.FilterData(
                        result, Excl(attr), Incl(attr), MaxLen(attr));

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
                // Logging service is effectively sync (ILogger.Log + optional callback).
                // Fire-and-observe: if LogAsync were truly async we still want to
                // proceed — business logic must not block on logging.
                _loggingService.Log(entry);
            }
        }

        private async Task InterceptAsync(MethodInfo method, object?[]? args)
        {
            var attr  = ResolveAttribute(method);
            var entry = BuildEntry(method, args, attr);
            var sw    = Stopwatch.StartNew();

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
            var attr  = ResolveAttribute(method);
            var entry = BuildEntry(method, args, attr);
            var sw    = Stopwatch.StartNew();
            TResult result;

            try
            {
                result = await ((Task<TResult>)CallTarget(method, args)!).ConfigureAwait(false);
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

            // Capture result only on success (outside finally) so errors aren't serialised.
            entry.MethodResult = _filterService.FilterData(
                result, Excl(attr), Incl(attr), MaxLen(attr));

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

        private LogEntry BuildEntry(MethodInfo method, object?[]? args, LogAttribute? attr)
        {
            var entry = new LogEntry
            {
                ServiceName   = typeof(T).Name,
                MethodName    = method.Name,
                Level         = attr?.Level ?? _options.DefaultLogLevel,
                CustomContext = attr?.CustomContext,
            };

            if (args is { Length: > 0 })
                entry.MethodArguments = _filterService.FilterData(
                    args, Excl(attr), Incl(attr), MaxLen(attr));

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

        private LogAttribute? ResolveAttribute(MethodInfo method)
            => (LogAttribute?)method.GetCustomAttributes(typeof(LogAttribute), true).FirstOrDefault()
               ?? (LogAttribute?)typeof(T).GetCustomAttributes(typeof(LogAttribute), true).FirstOrDefault();

        private static string[] Excl(LogAttribute? a) => a?.ExcludeProperties ?? Array.Empty<string>();
        private static string[] Incl(LogAttribute? a) => a?.IncludeProperties  ?? Array.Empty<string>();
        private        int      MaxLen(LogAttribute? a) => a?.MaxContentLength  ?? _options.DefaultMaxContentLength;
    }
}
