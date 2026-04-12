using System.Threading.Tasks;
using Dragonfire.Logging.Abstractions;
using Dragonfire.Logging.Attributes;
using Microsoft.Extensions.Logging;

namespace MyApp.Services
{
    public interface IOrderService
    {
        Task<string> GetOrderAsync([LogProperty("TenantId")] string tenantId, string orderId);

        [Log(MaxDepth = 0, Level = LogLevel.Debug)]
        void ProcessOrder(string orderId);

        [LogIgnore]
        string GetVersion();
    }

    public class OrderService : IOrderService, ILoggable
    {
        public Task<string> GetOrderAsync(string tenantId, string orderId)
            => Task.FromResult($"Order-{orderId}");

        public void ProcessOrder(string orderId) { }

        public string GetVersion() => "1.0";
    }
}
