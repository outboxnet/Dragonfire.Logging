using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using Dragonfire.Logging.Attributes;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Models;
using Dragonfire.Logging.Services;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Interceptors
{
    /// <summary>
    /// Castle DynamicProxy interceptor that wraps every public method on a service
    /// implementing <see cref="Abstractions.ILoggable"/>.
    /// <para>
    /// Extends <see cref="AsyncInterceptorBase"/> so both synchronous and async methods
    /// are handled correctly — async methods are awaited before logging the result;
    /// no blocking (.Result / .Wait) is ever used.
    /// </para>
    /// <para>
    /// Registered as Transient so it can safely consume Scoped dependencies
    /// (<see cref="IDragonfireLoggingService"/>, <see cref="ILogFilterService"/>)
    /// from the current DI scope.
    /// </para>
    /// </summary>
    public sealed class DragonfireInterceptor : AsyncInterceptorBase
    {
        private readonly IDragonfireLoggingService _loggingService;
        private readonly ILogFilterService _filterService;
        private readonly DragonfireLoggingOptions _options;

        public DragonfireInterceptor(
            IDragonfireLoggingService loggingService,
            ILogFilterService filterService,
            DragonfireLoggingOptions options)
        {
            _loggingService = loggingService;
            _filterService = filterService;
            _options = options;
        }

        // ── AsyncInterceptorBase overrides ───────────────────────────────────

        /// <summary>Called for async methods that return <see cref="Task"/> (no result value).</summary>
        protected override async Task InterceptAsync(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task> proceed)
        {
            var entry = CreateEntry(invocation);
            var sw = Stopwatch.StartNew();
            try
            {
                await proceed(invocation, proceedInfo).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ApplyError(entry, ex, _options.IncludeStackTraceOnError);
                throw;
            }
            finally
            {
                sw.Stop();
                entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                await _loggingService.LogAsync(entry).ConfigureAwait(false);
            }
        }

        /// <summary>Called for async methods that return <see cref="Task{TResult}"/>.</summary>
        protected override async Task<TResult> InterceptAsync<TResult>(
            IInvocation invocation,
            IInvocationProceedInfo proceedInfo,
            Func<IInvocation, IInvocationProceedInfo, Task<TResult>> proceed)
        {
            var entry = CreateEntry(invocation);
            var sw = Stopwatch.StartNew();
            TResult result;
            try
            {
                result = await proceed(invocation, proceedInfo).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ApplyError(entry, ex, _options.IncludeStackTraceOnError);
                throw;
            }
            finally
            {
                sw.Stop();
                entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                await _loggingService.LogAsync(entry).ConfigureAwait(false);
            }

            // Log result outside the finally so we only do it on success.
            entry.MethodResult = _filterService.FilterData(
                result,
                GetExclude(invocation),
                GetInclude(invocation),
                GetMaxLength(invocation));

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private LogEntry CreateEntry(IInvocation invocation)
        {
            var attr = GetAttribute(invocation);
            var exclude = GetExclude(invocation);
            var include = GetInclude(invocation);
            var maxLen = GetMaxLength(invocation);

            var entry = new LogEntry
            {
                ServiceName = invocation.TargetType?.Name
                              ?? invocation.Method.DeclaringType?.Name,
                MethodName = invocation.Method.Name,
                Level = attr?.Level ?? _options.DefaultLogLevel,
                CustomContext = attr?.CustomContext
            };

            if (invocation.Arguments.Length > 0)
            {
                entry.MethodArguments = _filterService.FilterData(
                    invocation.Arguments, exclude, include, maxLen);
            }

            return entry;
        }

        private static void ApplyError(LogEntry entry, Exception ex, bool includeStackTrace)
        {
            entry.IsError = true;
            entry.Level = LogLevel.Error;
            entry.ErrorMessage = ex.Message;
            if (includeStackTrace)
                entry.StackTrace = ex.StackTrace;
        }

        private static LogAttribute? GetAttribute(IInvocation invocation)
            => (LogAttribute?)invocation.Method
                   .GetCustomAttributes(typeof(LogAttribute), inherit: true)
                   .FirstOrDefault()
               ?? (LogAttribute?)invocation.TargetType?
                   .GetCustomAttributes(typeof(LogAttribute), inherit: true)
                   .FirstOrDefault();

        private static string[] GetExclude(IInvocation inv)
            => GetAttribute(inv)?.ExcludeProperties ?? Array.Empty<string>();

        private static string[] GetInclude(IInvocation inv)
            => GetAttribute(inv)?.IncludeProperties ?? Array.Empty<string>();

        private int GetMaxLength(IInvocation inv)
            => GetAttribute(inv)?.MaxContentLength ?? _options.DefaultMaxContentLength;
    }
}
