using System;
using System.Linq;
using Castle.DynamicProxy;
using Dragonfire.Logging.Abstractions;
using Dragonfire.Logging.Configuration;
using Dragonfire.Logging.Interceptors;
using Dragonfire.Logging.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dragonfire.Logging.Extensions
{
    public static class DragonfireLoggingExtensions
    {
        /// <summary>
        /// Registers Dragonfire.Logging core services:
        /// <list type="bullet">
        ///   <item><see cref="IDragonfireLoggingService"/> — structured log writer with correlation-ID context.</item>
        ///   <item><see cref="ILogFilterService"/> — payload sanitiser and sensitive-data redactor.</item>
        ///   <item>Castle <see cref="ProxyGenerator"/> (singleton).</item>
        ///   <item><see cref="DragonfireInterceptor"/> (transient) — service-layer proxy.</item>
        /// </list>
        /// When <see cref="DragonfireLoggingOptions.EnableServiceInterception"/> is <c>true</c>,
        /// all services registered <b>before</b> this call whose implementation implements
        /// <see cref="ILoggable"/> are automatically decorated via Scrutor.
        ///
        /// For HTTP request/response logging also call
        /// <c>AddDragonfireAspNetCore()</c> from the <c>Dragonfire.Logging.AspNetCore</c> package.
        /// </summary>
        public static IServiceCollection AddDragonfireLogging(
            this IServiceCollection services,
            Action<DragonfireLoggingOptions>? configure = null)
        {
            var options = new DragonfireLoggingOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton(options.SensitiveDataPolicy);

            // ProxyGenerator compiles IL to build proxy types — must be singleton.
            services.TryAddSingleton<ProxyGenerator>();

            services.TryAdd(ServiceDescriptor.Describe(
                typeof(IDragonfireLoggingService),
                typeof(DragonfireLoggingService),
                options.LoggingServiceLifetime));

            // LogFilterService is stateless — singleton avoids repeated allocation.
            services.TryAddSingleton<ILogFilterService, LogFilterService>();

            // Interceptor is Transient so it safely consumes Scoped services per request.
            services.TryAddTransient<DragonfireInterceptor>();

            if (options.EnableServiceInterception)
                services.DecorateLoggableServices();

            return services;
        }

        /// <summary>
        /// Decorates every already-registered interface service whose concrete
        /// implementation implements <see cref="ILoggable"/> with a Castle DynamicProxy
        /// that transparently logs all method calls.
        ///
        /// Called automatically by <see cref="AddDragonfireLogging"/> when
        /// <see cref="DragonfireLoggingOptions.EnableServiceInterception"/> is <c>true</c>,
        /// or invoke it directly for manual control.
        /// </summary>
        public static IServiceCollection DecorateLoggableServices(this IServiceCollection services)
        {
            var targets = services
                .Where(d =>
                    d.ServiceType.IsInterface &&
                    d.ImplementationType != null &&
                    typeof(ILoggable).IsAssignableFrom(d.ImplementationType))
                .Select(d => d.ServiceType)
                .Distinct()
                .ToList();

            foreach (var serviceType in targets)
            {
                // Scrutor's Decorate keeps the original implementation as "inner";
                // the proxy delegates every call to it after logging.
                services.Decorate(serviceType, (inner, provider) =>
                {
                    var generator   = provider.GetRequiredService<ProxyGenerator>();
                    var interceptor = provider.GetRequiredService<DragonfireInterceptor>();
                    return generator.CreateInterfaceProxyWithTarget(
                        serviceType, inner, interceptor.ToInterceptor());
                });
            }

            return services;
        }

        // ── IDragonfireLoggingService fluent helpers ──────────────────────────

        /// <summary>
        /// Attach a user identity to the ambient logging context for
        /// <paramref name="correlationId"/> so every subsequent
        /// <see cref="Models.LogEntry"/> in this scope carries <c>UserId</c>.
        /// Returns the service for fluent chaining.
        /// </summary>
        public static IDragonfireLoggingService WithCorrelationId(
            this IDragonfireLoggingService service,
            string correlationId,
            string? userId = null)
        {
            var ctx = service.GetOrCreateContext(correlationId);
            if (!string.IsNullOrEmpty(userId))
                ctx.UserId = userId;
            return service;
        }
    }
}
