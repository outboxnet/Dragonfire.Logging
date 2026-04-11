using System;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Attributes
{
    /// <summary>
    /// Controls logging behaviour for a controller, action, or service class/method.
    /// When placed on a class it acts as a default for all its members; a method-level
    /// attribute always takes precedence over a class-level one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class LogAttribute : Attribute
    {
        /// <summary>Log the inbound request / method arguments. Default: <c>true</c>.</summary>
        public bool LogRequest { get; set; } = true;

        /// <summary>Log the outbound response / method return value. Default: <c>true</c>.</summary>
        public bool LogResponse { get; set; } = true;

        /// <summary>Log ASP.NET Core model-validation errors. Default: <c>true</c>.</summary>
        public bool LogValidationErrors { get; set; } = true;

        /// <summary>
        /// Property names to suppress from logged payloads.
        /// Supports dot-notation for nested properties, e.g. <c>"User.Password"</c>.
        /// </summary>
        public string[] ExcludeProperties { get; set; } = Array.Empty<string>();

        /// <summary>
        /// When non-empty, only these property names are included in logged payloads
        /// (takes precedence over <see cref="ExcludeProperties"/>).
        /// </summary>
        public string[] IncludeProperties { get; set; } = Array.Empty<string>();

        /// <summary>Severity level written to the log sink. Default: <c>Information</c>.</summary>
        public LogLevel Level { get; set; } = LogLevel.Information;

        /// <summary>Optional free-text annotation added to every log entry for this target.</summary>
        public string? CustomContext { get; set; }

        /// <summary>
        /// Truncate logged payload strings to this many characters (0 = unlimited).
        /// Default: <c>0</c>.
        /// </summary>
        public int MaxContentLength { get; set; } = 0;

        /// <summary>Include HTTP request headers in the log entry. Default: <c>false</c>.</summary>
        public bool LogHeaders { get; set; } = false;

        /// <summary>
        /// Whitelist of header names to log when <see cref="LogHeaders"/> is <c>true</c>.
        /// Empty means all headers (minus <see cref="ExcludeHeaders"/>).
        /// </summary>
        public string[] IncludeHeaders { get; set; } = Array.Empty<string>();

        /// <summary>Header names always suppressed from logs. Defaults to sensitive auth headers.</summary>
        public string[] ExcludeHeaders { get; set; } = { "Authorization", "Cookie", "X-API-Key" };
    }
}
