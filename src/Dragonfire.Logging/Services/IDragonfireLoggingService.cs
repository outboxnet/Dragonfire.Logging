using System.Threading.Tasks;
using Dragonfire.Logging.Models;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Services
{
    /// <summary>
    /// Core logging service. Receives structured <see cref="LogEntry"/> objects from
    /// the HTTP filter and service interceptors, writes them via the standard
    /// <see cref="ILogger"/> infrastructure, and optionally forwards them to a custom sink.
    /// </summary>
    public interface IDragonfireLoggingService
    {
        /// <summary>
        /// Synchronous write — used by the proxy for intercepted synchronous methods
        /// so the call never blocks on an async continuation.
        /// </summary>
        void Log(LogEntry entry);

        /// <summary>Persist a fully-built log entry asynchronously.</summary>
        Task LogAsync(LogEntry entry);

        /// <summary>
        /// Emit an ad-hoc log entry mid-request, enriched with the request's ambient context.
        /// Useful for business-level checkpoints (e.g. "order validated", "payment authorised").
        /// </summary>
        Task LogCustomAsync(string correlationId, string message, object? data = null,
            LogLevel level = LogLevel.Information);

        /// <summary>Attach an arbitrary key/value pair to the current request's log context.</summary>
        void AddCustomData(string correlationId, string key, object value);

        /// <summary>Get (or lazily create) the ambient context for <paramref name="correlationId"/>.</summary>
        LoggingContext GetOrCreateContext(string correlationId);

        /// <summary>Discard the context for <paramref name="correlationId"/>.</summary>
        void ClearContext(string correlationId);
    }
}
