using System;
using Dragonfire.Logging.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Configuration
{
    /// <summary>
    /// Core configuration for Dragonfire.Logging.
    /// Controls service-layer interception and shared concerns such as
    /// redaction policy, log level, and payload truncation.
    ///
    /// HTTP request/response options live in
    /// <c>Dragonfire.Logging.AspNetCore.Configuration.DragonfireAspNetCoreOptions</c>.
    /// </summary>
    public sealed class DragonfireLoggingOptions
    {
        // ── Service interception ──────────────────────────────────────────────

        /// <summary>
        /// When <c>true</c>, every service registered before
        /// <c>AddDragonfireLogging</c> whose implementation implements
        /// <see cref="Abstractions.ILoggable"/> is automatically wrapped with a
        /// Castle DynamicProxy logging interceptor via Scrutor's <c>Decorate</c>.
        /// Default: <c>false</c>.
        /// </summary>
        public bool EnableServiceInterception { get; set; } = false;

        // ── Shared ────────────────────────────────────────────────────────────

        /// <summary>
        /// Maximum characters serialised for any single payload (0 = unlimited).
        /// Can be overridden per method via <see cref="Attributes.LogAttribute.MaxContentLength"/>.
        /// Default: <c>10 000</c>.
        /// </summary>
        public int DefaultMaxContentLength { get; set; } = 10_000;

        /// <summary>Include the full stack trace in error log entries. Default: <c>true</c>.</summary>
        public bool IncludeStackTraceOnError { get; set; } = true;

        /// <summary>Severity used when no <see cref="Attributes.LogAttribute"/> is present. Default: <c>Information</c>.</summary>
        public LogLevel DefaultLogLevel { get; set; } = LogLevel.Information;

        /// <summary>Sensitive-data redaction policy applied to all logged payloads.</summary>
        public SensitiveDataPolicy SensitiveDataPolicy { get; set; } = new();

        /// <summary>
        /// Optional callback invoked for every <see cref="LogEntry"/> after the
        /// standard <see cref="ILogger"/> write. Forward entries to a custom sink
        /// (database, message bus, OpenTelemetry, etc.) here.
        /// </summary>
        public Action<LogEntry>? CustomLogAction { get; set; }

        /// <summary>DI lifetime used for <c>IDragonfireLoggingService</c>. Default: <c>Scoped</c>.</summary>
        public ServiceLifetime LoggingServiceLifetime { get; set; } = ServiceLifetime.Scoped;
    }
}
