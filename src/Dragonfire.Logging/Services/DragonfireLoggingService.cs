using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Dragonfire.Logging.Services
{
    /// <inheritdoc cref="IDragonfireLoggingService"/>
    public sealed class DragonfireLoggingService : IDragonfireLoggingService
    {
        private readonly ILogger<DragonfireLoggingService> _logger;
        private readonly DragonfireLoggingOptions _options;

        // Thread-safe context store — keyed by correlation ID.
        // With the default Scoped lifetime this dictionary lives for one HTTP request.
        private readonly ConcurrentDictionary<string, LoggingContext> _contexts = new();

        public DragonfireLoggingService(
            ILogger<DragonfireLoggingService> logger,
            DragonfireLoggingOptions options)
        {
            _logger  = logger;
            _options = options;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Every field of <paramref name="entry"/> is emitted as a <b>named structured
        /// property</b> via <c>ILogger.BeginScope</c> so that any structured-logging
        /// provider (Application Insights, Seq, ELK, Loki…) can index and filter on
        /// individual fields without any provider-specific configuration.
        ///
        /// In Application Insights the properties appear under
        /// <c>customDimensions["Dragonfire.*"]</c> and are queryable in KQL:
        /// <code>
        /// traces
        /// | where customDimensions["Dragonfire.CorrelationId"] == "abc-123"
        /// | where customDimensions["Dragonfire.ServiceName"]   == "OrderService"
        /// | where toint(customDimensions["Dragonfire.ElapsedMs"]) > 500
        /// </code>
        /// </remarks>
        public void Log(LogEntry entry)
        {
            // BeginScope pushes every key/value into the structured-logging context
            // so providers that support it (AppInsights, Serilog, Seq, OTEL…) index
            // them as first-class queryable fields rather than opaque blobs.
            using (_logger.BeginScope(BuildScope(entry)))
            {
                if (entry.IsError)
                    _logger.Log(entry.Level,
                        "[Dragonfire] {Source} FAILED in {ElapsedMs}ms — {ErrorMessage}",
                        GetSource(entry),
                        entry.ElapsedMilliseconds,
                        entry.ErrorMessage);
                else
                    _logger.Log(entry.Level,
                        "[Dragonfire] {Source} completed in {ElapsedMs}ms {StatusCode}",
                        GetSource(entry),
                        entry.ElapsedMilliseconds,
                        entry.StatusCode);
            }

            _options.CustomLogAction?.Invoke(entry);
        }

        /// <inheritdoc/>
        public Task LogAsync(LogEntry entry)
        {
            Log(entry);
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task LogCustomAsync(
            string correlationId,
            string message,
            object? data  = null,
            LogLevel level = LogLevel.Information)
        {
            var ctx = GetOrCreateContext(correlationId);

            return LogAsync(new LogEntry
            {
                CorrelationId = correlationId,
                Level         = level,
                CustomContext = message,
                RequestData   = data,
                UserId        = ctx.UserId,
                CustomData    = ctx.CustomData.Count > 0 ? new Dictionary<string, object>(ctx.CustomData) : null
            });
        }

        /// <inheritdoc/>
        public void AddCustomData(string correlationId, string key, object value)
            => GetOrCreateContext(correlationId).CustomData[key] = value;

        /// <inheritdoc/>
        public LoggingContext GetOrCreateContext(string correlationId)
            => _contexts.GetOrAdd(correlationId, id => new LoggingContext { CorrelationId = id });

        /// <inheritdoc/>
        public void ClearContext(string correlationId)
            => _contexts.TryRemove(correlationId, out _);

        // ── Structured scope builder ─────────────────────────────────────────

        /// <summary>
        /// Builds a flat dictionary of every non-null <see cref="LogEntry"/> field.
        /// Each entry becomes an individual structured property in the logging context.
        ///
        /// Naming convention: <c>Dragonfire.{PropertyName}</c>
        ///   • No collision with ASP.NET Core built-ins (<c>RequestId</c>, <c>RequestPath</c>…).
        ///   • Queryable in AppInsights KQL as <c>customDimensions["Dragonfire.X"]</c>.
        ///   • Works with Seq property filters, Grafana Loki label selectors, etc.
        ///
        /// Complex objects (request/response bodies, method arguments) are serialised
        /// to compact JSON strings so every provider stores them as a single indexed field.
        ///
        /// <see cref="CustomData"/> entries are flattened to individual
        /// <c>Dragonfire.Custom.{Key}</c> properties for fine-grained filtering.
        /// </summary>
        private static Dictionary<string, object> BuildScope(LogEntry entry)
        {
            var scope = new Dictionary<string, object>();

            // ── Identity ────────────────────────────────────────────────────
            AddIfSet(scope, "Dragonfire.CorrelationId",    entry.CorrelationId);
            AddIfSet(scope, "Dragonfire.TraceId",          entry.TraceId);
            AddIfSet(scope, "Dragonfire.UserId",           entry.UserId);
            AddIfSet(scope, "Dragonfire.CustomContext",    entry.CustomContext);

            // ── HTTP context ────────────────────────────────────────────────
            AddIfSet(scope, "Dragonfire.HttpMethod",       entry.HttpMethod);
            AddIfSet(scope, "Dragonfire.Path",             entry.Path);
            AddIfSet(scope, "Dragonfire.QueryString",      entry.QueryString);
            AddIfSet(scope, "Dragonfire.ClientIp",         entry.ClientIp);
            AddIfSet(scope, "Dragonfire.UserAgent",        entry.UserAgent);

            if (entry.StatusCode.HasValue)
                scope["Dragonfire.StatusCode"] = entry.StatusCode.Value;

            // ── Service layer ───────────────────────────────────────────────
            AddIfSet(scope, "Dragonfire.ServiceName",      entry.ServiceName);
            AddIfSet(scope, "Dragonfire.MethodName",       entry.MethodName);

            // ── Performance ─────────────────────────────────────────────────
            scope["Dragonfire.ElapsedMs"] = entry.ElapsedMilliseconds;

            // ── Error ────────────────────────────────────────────────────────
            if (entry.IsError)
            {
                scope["Dragonfire.IsError"]      = true;
                AddIfSet(scope, "Dragonfire.ErrorMessage", entry.ErrorMessage);
                AddIfSet(scope, "Dragonfire.StackTrace",   entry.StackTrace);
            }

            // ── Payloads — serialised to JSON strings ────────────────────────
            // Providers store them as a single indexed string field; tools like
            // AppInsights Analytics, Seq, Kibana can still substring-search inside them.
            AddJson(scope,  "Dragonfire.RequestData",      entry.RequestData);
            AddJson(scope,  "Dragonfire.ResponseData",     entry.ResponseData);
            AddJson(scope,  "Dragonfire.MethodArguments",  entry.MethodArguments);
            AddJson(scope,  "Dragonfire.MethodResult",     entry.MethodResult);

            // ── Custom data — flattened to individual properties ─────────────
            // Each key becomes its own filterable dimension, e.g.:
            //   customDimensions["Dragonfire.Custom.TenantId"] == "acme"
            if (entry.CustomData is { Count: > 0 })
            {
                foreach (var (key, value) in entry.CustomData)
                {
                    if (value is not null)
                        scope[$"Dragonfire.Custom.{key}"] = value;
                }
            }

            return scope;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string GetSource(LogEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.ServiceName))
            {
                return string.IsNullOrEmpty(entry.MethodName)
                    ? entry.ServiceName
                    : $"{entry.ServiceName}.{entry.MethodName}";
            }

            return string.IsNullOrEmpty(entry.HttpMethod)
                ? (entry.Path ?? "unknown")
                : $"{entry.HttpMethod} {entry.Path}";
        }

        private static void AddIfSet(Dictionary<string, object> scope, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                scope[key] = value;
        }

        private static void AddJson(Dictionary<string, object> scope, string key, object? value)
        {
            if (value is null) return;

            string json;
            if (value is string s)
            {
                // Already a string (e.g. already-filtered JSON from LogFilterService).
                json = s;
            }
            else
            {
                try   { json = JsonConvert.SerializeObject(value, Formatting.None); }
                catch { json = value.ToString() ?? string.Empty; }
            }

            if (!string.IsNullOrWhiteSpace(json))
                scope[key] = json;
        }
    }
}
