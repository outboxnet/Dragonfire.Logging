using Dragonfire.Logging.Attributes;

namespace SampleApp.Services
{
    public class Order
    {
        public string OrderId { get; set; }

        [LogProperty]
        public decimal Price { get; set; }
        public string TenantId { get; set; }
    }
}
