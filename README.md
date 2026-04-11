# Dragonfire.Logging

Production-ready structured logging for ASP.NET Core.

- **HTTP layer** — automatic request/response logging via a global MVC action filter.
- **Service layer** — opt-in, zero-boilerplate method interception via Castle DynamicProxy, activated by implementing `ILoggable`.
- **Sensitive data redaction** — field-name matching, regex patterns (credit cards, SSNs, JWTs), email and phone redaction, configurable per endpoint/method via `[Log]`.

## Quick start

```csharp
// Program.cs — register your services first, then add logging
builder.Services.AddScoped<IOrderService, OrderService>(); // OrderService : ILoggable

builder.Services.AddDragonfireLogging(opt =>
{
    opt.EnableServiceInterception = true;   // decorate ILoggable services
    opt.DefaultMaxContentLength = 5_000;    // truncate large payloads
    opt.CustomLogAction = entry =>          // optional custom sink
        myDb.InsertLogEntry(entry);
});
```

## ILoggable — service-layer interception

Implement `ILoggable` on any service class to have every public method automatically logged:

```csharp
public class OrderService : IOrderService, ILoggable
{
    public async Task<Order> CreateOrderAsync(CreateOrderRequest request) { ... }
}
```

Scrutor's `Decorate` wraps the DI registration with a Castle DynamicProxy — no code changes needed in the service itself.

## [Log] attribute

Fine-tune logging on controllers, actions, or service methods:

```csharp
[Log(ExcludeProperties = new[] { "CardNumber", "Cvv" }, Level = LogLevel.Debug)]
public async Task<IActionResult> Checkout(CheckoutRequest request) { ... }
```

## [LogIgnore] attribute

Suppress individual properties regardless of global configuration:

```csharp
public class UserDto
{
    public string Name { get; set; }

    [LogIgnore("PCI compliance")]
    public string CardNumber { get; set; }
}
```

## Sensitive data policy

Default redactions (applied globally):

| Type | Example input | Logged as |
|---|---|---|
| Sensitive field names | `"password": "s3cr3t"` | property removed |
| Credit card numbers | `4111111111111111` | `[CREDIT_CARD_REDACTED]` |
| SSNs | `123-45-6789` | `[SSN_REDACTED]` |
| JWT Bearer tokens | `Bearer eyJ…` | `Bearer [JWT_REDACTED]` |
| Email addresses | `user@example.com` | `[EMAIL_REDACTED]` |
| Phone numbers | `555-867-5309` | `[PHONE_REDACTED]` |

## Requirements

- .NET 8.0+
- ASP.NET Core (via `Microsoft.AspNetCore.App` framework reference)

## License

MIT
