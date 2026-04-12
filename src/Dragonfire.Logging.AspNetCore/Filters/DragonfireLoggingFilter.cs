using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Dragonfire.Logging.AspNetCore.Configuration;
using Dragonfire.Logging.Attributes;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Models;
using Dragonfire.Logging.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.AspNetCore.Filters
{
    /// <summary>
    /// Global MVC action filter for automatic HTTP request/response logging.
    ///
    /// Works with <b>both</b> ASP.NET Core Web API (controller-based) and
    /// ASP.NET Core MVC (views + controllers). It does <b>not</b> apply to
    /// minimal-API endpoints — use <see cref="Middleware.DragonfireLoggingMiddleware"/>
    /// for those.
    ///
    /// Response data is read from <see cref="ObjectResult.Value"/> so the response
    /// body stream is never replaced, keeping the middleware pipeline safe.
    ///
    /// Registered at <c>int.MinValue</c> order so it wraps all other filters
    /// and measures total action elapsed time accurately.
    ///
    /// <b>[LogProperty] support:</b>
    /// Action parameters decorated with <c>[LogProperty]</c> and properties on any
    /// action-argument DTO decorated with <c>[LogProperty]</c> are promoted to
    /// individual structured-log scope entries (e.g. <c>customDimensions["TenantId"]</c>).
    ///
    /// <b>Depth control:</b>
    /// By default only 1 level of nesting is serialised. Nested objects become
    /// <c>[N fields]</c> and arrays become <c>[N items]</c>. Override per endpoint
    /// via <c>[Log(MaxDepth = 0)]</c> (unlimited) or globally via
    /// <see cref="DragonfireLoggingOptions.DefaultMaxDepth"/>.
    /// </summary>
    public sealed class DragonfireLoggingFilter : IAsyncActionFilter
    {
        private const string CorrelationIdHeader = "X-Correlation-ID";

        private readonly IDragonfireLoggingService       _loggingService;
        private readonly ILogFilterService               _filterService;
        private readonly DragonfireLoggingOptions        _coreOptions;
        private readonly DragonfireAspNetCoreOptions     _httpOptions;
        private readonly ILogger<DragonfireLoggingFilter> _logger;

        public DragonfireLoggingFilter(
            IDragonfireLoggingService loggingService,
            ILogFilterService filterService,
            DragonfireLoggingOptions coreOptions,
            DragonfireAspNetCoreOptions httpOptions,
            ILogger<DragonfireLoggingFilter> logger)
        {
            _loggingService = loggingService;
            _filterService  = filterService;
            _coreOptions    = coreOptions;
            _httpOptions    = httpOptions;
            _logger         = logger;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            // Skip paths that should never be logged (health checks, swagger, etc.)
            var path = context.HttpContext.Request.Path.Value ?? string.Empty;
            if (_httpOptions.ExcludePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            var attr          = ResolveLogAttribute(context);
            var correlationId = EnsureCorrelationId(context.HttpContext);
            var maxDepth      = ResolveMaxDepth(attr);

            var entry = new LogEntry
            {
                CorrelationId = correlationId,
                TraceId       = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier,
                HttpMethod    = context.HttpContext.Request.Method,
                Path          = path,
                QueryString   = context.HttpContext.Request.QueryString.ToString(),
                ClientIp      = context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent     = context.HttpContext.Request.Headers["User-Agent"].ToString(),
                Level         = attr?.Level ?? _coreOptions.DefaultLogLevel,
                CustomContext = attr?.CustomContext
            };

            // Merge ambient per-request context (UserId, custom key/values).
            var ctx = _loggingService.GetOrCreateContext(correlationId);
            entry.UserId     = ctx.UserId;
            entry.CustomData = ctx.CustomData.Count > 0 ? new Dictionary<string, object>(ctx.CustomData) : null;

            if (_httpOptions.EnableRequestLogging && attr?.LogRequest != false)
                entry.RequestData = await BuildRequestDataAsync(context, attr, maxDepth);

            // Extract [LogProperty] from action parameters and argument objects (before execution).
            ExtractArgumentNamedProperties(entry, context);

            // ── Execute the action ────────────────────────────────────────────
            var sw      = Stopwatch.StartNew();
            var executed = await next();
            sw.Stop();

            entry.ElapsedMilliseconds = sw.ElapsedMilliseconds;
            entry.StatusCode          = executed.HttpContext.Response.StatusCode;

            if (executed.Exception is { } ex)
            {
                entry.IsError      = true;
                entry.Level        = LogLevel.Error;
                entry.ErrorMessage = ex.Message;
                if (_coreOptions.IncludeStackTraceOnError)
                    entry.StackTrace = ex.StackTrace;
            }
            else if (_httpOptions.EnableResponseLogging && attr?.LogResponse != false)
            {
                // Build filtered response payload.
                entry.ResponseData = BuildResponseData(executed, attr, maxDepth);

                // Extract [LogProperty] from response DTO properties.
                if (executed.Result is ObjectResult { Value: not null } objResult)
                    MergeIntoNamedProperties(entry, _filterService.ExtractNamedProperties(objResult.Value));
            }

            if (_httpOptions.LogValidationErrors && attr?.LogValidationErrors != false
                && !context.ModelState.IsValid)
            {
                entry.CustomData ??= new Dictionary<string, object>();
                entry.CustomData["ValidationErrors"] = context.ModelState
                    .Where(kv => kv.Value?.Errors.Count > 0)
                    .ToDictionary(
                        kv => kv.Key,
                        kv => (object)kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
            }

            await _loggingService.LogAsync(entry);
            _loggingService.ClearContext(correlationId);
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>Method-level [Log] takes precedence over class-level.</summary>
        private static LogAttribute? ResolveLogAttribute(ActionExecutingContext context)
            => context.ActionDescriptor.EndpointMetadata.OfType<LogAttribute>().FirstOrDefault()
               ?? context.Controller?.GetType()
                   .GetCustomAttributes(typeof(LogAttribute), inherit: true)
                   .FirstOrDefault() as LogAttribute;

        private string EnsureCorrelationId(HttpContext http)
        {
            if (http.Request.Headers.TryGetValue(CorrelationIdHeader, out var existing)
                && !string.IsNullOrWhiteSpace(existing))
                return existing.ToString();

            var id = Guid.NewGuid().ToString();
            http.Response.Headers[CorrelationIdHeader] = id;
            return id;
        }

        /// <summary>
        /// Resolves effective max depth: attribute-level (when ≥ 0) takes precedence
        /// over <see cref="DragonfireLoggingOptions.DefaultMaxDepth"/>.
        /// </summary>
        private int ResolveMaxDepth(LogAttribute? attr)
            => (attr is not null && attr.MaxDepth >= 0)
                ? attr.MaxDepth
                : _coreOptions.DefaultMaxDepth;

        private async Task<object?> BuildRequestDataAsync(
            ActionExecutingContext context, LogAttribute? attr, int maxDepth)
        {
            var data      = new Dictionary<string, object?>();
            var maxLength = attr?.MaxContentLength ?? _coreOptions.DefaultMaxContentLength;

            if (context.ActionArguments.Count > 0)
            {
                data["Arguments"] = _filterService.FilterData(
                    context.ActionArguments,
                    attr?.ExcludeProperties,
                    attr?.IncludeProperties,
                    maxLength,
                    maxDepth);
            }

            var req = context.HttpContext.Request;
            if (!HttpMethods.IsGet(req.Method) && req.ContentLength > 0)
            {
                req.EnableBuffering();
                var body = await new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true)
                    .ReadToEndAsync();
                req.Body.Position = 0;

                if (!string.IsNullOrEmpty(body))
                    data["Body"] = _filterService.FilterString(body, maxLength);
            }

            if (attr?.LogHeaders == true)
            {
                var headers = context.HttpContext.Request.Headers
                    .Where(h => !(attr.ExcludeHeaders?.Contains(h.Key, StringComparer.OrdinalIgnoreCase) ?? false))
                    .ToDictionary(h => h.Key, h => h.Value.ToString());

                if (attr.IncludeHeaders?.Length > 0)
                    headers = headers
                        .Where(h => attr.IncludeHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                        .ToDictionary(h => h.Key, h => h.Value);

                if (headers.Count > 0)
                    data["Headers"] = headers;
            }

            return data.Count > 0 ? data : null;
        }

        private object? BuildResponseData(ActionExecutedContext context, LogAttribute? attr, int maxDepth)
        {
            if (context.Result is not ObjectResult { Value: not null } objectResult)
                return null;

            return _filterService.FilterData(
                objectResult.Value,
                attr?.ExcludeProperties,
                attr?.IncludeProperties,
                attr?.MaxContentLength ?? _coreOptions.DefaultMaxContentLength,
                maxDepth);
        }

        // ── [LogProperty] extraction ─────────────────────────────────────────

        /// <summary>
        /// Populates <see cref="LogEntry.NamedProperties"/> from:
        /// <list type="number">
        ///   <item>Action method parameters decorated with <c>[LogProperty]</c>.</item>
        ///   <item>Properties on each action-argument DTO decorated with <c>[LogProperty]</c>.</item>
        /// </list>
        /// </summary>
        private void ExtractArgumentNamedProperties(LogEntry entry, ActionExecutingContext context)
        {
            var named = entry.NamedProperties
                        ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // 1. Parameter-level [LogProperty] — available via ControllerActionDescriptor.
            if (context.ActionDescriptor is ControllerActionDescriptor cad)
            {
                foreach (var param in cad.MethodInfo.GetParameters())
                {
                    var logProp = param.GetCustomAttribute<LogPropertyAttribute>();
                    if (logProp is null) continue;

                    var key = logProp.Name ?? param.Name ?? $"param{param.Position}";
                    if (param.Name is not null
                        && context.ActionArguments.TryGetValue(param.Name, out var argValue))
                    {
                        named.TryAdd(key, argValue);
                    }
                }
            }

            // 2. Property-level [LogProperty] on each argument object.
            foreach (var arg in context.ActionArguments.Values)
                MergeDict(named, _filterService.ExtractNamedProperties(arg));

            if (named.Count > 0)
                entry.NamedProperties = named;
        }

        private static void MergeIntoNamedProperties(LogEntry entry, Dictionary<string, object?> source)
        {
            if (source.Count == 0) return;
            var named = entry.NamedProperties
                        ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            MergeDict(named, source);
            entry.NamedProperties = named;
        }

        private static void MergeDict(
            Dictionary<string, object?> target,
            Dictionary<string, object?> source)
        {
            foreach (var (k, v) in source)
                target.TryAdd(k, v);
        }
    }
}
