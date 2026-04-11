using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Models;
using Microsoft.Extensions.Logging;

namespace Dragonfire.Logging.Services
{
    /// <inheritdoc cref="IDragonfireLoggingService"/>
    public sealed class DragonfireLoggingService : IDragonfireLoggingService
    {
        private readonly ILogger<DragonfireLoggingService> _logger;
        private readonly DragonfireLoggingOptions _options;

        // Thread-safe context store — keyed by correlation ID.
        // With the default Scoped lifetime this dictionary lives for one HTTP request,
        // so it stays small and is GC'd automatically at end-of-scope.
        private readonly ConcurrentDictionary<string, LoggingContext> _contexts = new();

        public DragonfireLoggingService(
            ILogger<DragonfireLoggingService> logger,
            DragonfireLoggingOptions options)
        {
            _logger = logger;
            _options = options;
        }

        public Task LogAsync(LogEntry entry)
        {
            _logger.Log(entry.Level, "{@LogEntry}", entry);
            _options.CustomLogAction?.Invoke(entry);
            return Task.CompletedTask;
        }

        public Task LogCustomAsync(string correlationId, string message, object? data = null,
            LogLevel level = LogLevel.Information)
        {
            var context = GetOrCreateContext(correlationId);

            var entry = new LogEntry
            {
                CorrelationId = correlationId,
                Level = level,
                CustomContext = message,
                RequestData = data,
                UserId = context.UserId,
                CustomData = context.CustomData.Count > 0 ? context.CustomData : null
            };

            return LogAsync(entry);
        }

        public void AddCustomData(string correlationId, string key, object value)
            => GetOrCreateContext(correlationId).CustomData[key] = value;

        public LoggingContext GetOrCreateContext(string correlationId)
            => _contexts.GetOrAdd(correlationId, id => new LoggingContext { CorrelationId = id });

        public void ClearContext(string correlationId)
            => _contexts.TryRemove(correlationId, out _);
    }
}
