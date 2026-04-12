# Dragonfire.Logging

Production-ready, zero-boilerplate **structured logging** for .NET 8+ services and ASP.NET Core APIs.

```
Dragonfire.Logging              — core (framework-agnostic)
Dragonfire.Logging.AspNetCore   — HTTP request/response logging
Dragonfire.Logging.Generator    — Roslyn source generator (compile-time proxies)
```

Every log entry is written as **individually named structured properties** — not one opaque JSON blob — so you can filter, alert and build dashboards against each field directly in Application Insights, Seq, Loki, Datadog, or any ILogger-compatible sink.

---

## Table of contents

1. [What problem does this solve?](#1-what-problem-does-this-solve)
2. [Package overview](#2-package-overview)
3. [Quick start — core + ASP.NET Core](#3-quick-start--core--aspnet-core)
4. [Service-layer interception — runtime proxy](#4-service-layer-interception--runtime-proxy)
5. [Source generator — compile-time proxy](#5-source-generator--compile-time-proxy)
   - [Why the generator exists](#why-the-generator-exists)
   - [Installation](#installation)
   - [How it works](#how-it-works)
   - [What gets generated](#what-gets-generated)
   - [DI registration](#di-registration)
6. [Structured logging and Application Insights](#6-structured-logging-and-application-insights)
7. [[LogProperty] — promote fields to first-class dimensions](#7-logproperty--promote-fields-to-first-class-dimensions)
8. [Payload depth control](#8-payload-depth-control)
9. [[Log] attribute reference](#9-log-attribute-reference)
10. [Sensitive data redaction](#10-sensitive-data-redaction)
11. [HTTP logging (ASP.NET Core)](#11-http-logging-aspnet-core)
12. [Configuration reference](#12-configuration-reference)
13. [Requirements](#13-requirements)

---

## 1. What problem does this solve?

Writing logging code by hand is tedious, error-prone, and almost never consistent:

```csharp
// What every team ends up with — scattered, inconsistent, incomplete
public async Task<Order> CreateOrderAsync(string tenantId, CreateOrderRequest req)
{
    _logger.LogInformation("CreateOrder called with tenantId={TenantId}", tenantId);
    var sw = Stopwatch.StartNew();
    try
    {
        var result = await _db.InsertAsync(req);
        _logger.LogInformation("CreateOrder succeeded in {Ms}ms", sw.ElapsedMilliseconds);
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "CreateOrder failed");
        throw;
    }
}
```

Dragonfire.Logging automates this at either **runtime** (via a `DispatchProxy` wrapper registered in DI) or at **compile time** (via a Roslyn source generator that emits a concrete proxy class). In both cases:

- Every method call is timed and logged with a consistent structured format
- Exceptions are captured with stack traces and the `IsError` flag set
- Request arguments and return values are serialised, filtered, and depth-limited automatically
- Fields you mark with `[LogProperty]` surface as individual `customDimensions` entries — queryable in KQL without string-parsing

---

## 2. Package overview

| Package | Targets | Purpose |
|---|---|---|
| `Dragonfire.Logging` | `net8.0` | Core: `ILoggable`, attributes, `DragonfireProxy<T>`, structured `ILogger` output |
| `Dragonfire.Logging.AspNetCore` | `net8.0` | HTTP filter + middleware for controller and minimal-API request/response logging |
| `Dragonfire.Logging.Generator` | `netstandard2.0` (analyzer) | Roslyn source generator — emits compile-time concrete proxy classes, zero runtime reflection |

All three packages have **no runtime dependency on Castle.Core, Scrutor, or any other AOP library**. The core and AspNetCore packages depend only on `Microsoft.Extensions.*` abstractions and `Newtonsoft.Json`.

---

## 3. Quick start — core + ASP.NET Core

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// 1. Register your services
builder.Services.AddScoped<IOrderService, OrderService>();   // OrderService : ILoggable

// 2. Add Dragonfire — core + HTTP
builder.Services.AddDragonfireAspNetCore(
    core: opt =>
    {
        opt.EnableServiceInterception = true;   // wrap ILoggable services with the runtime proxy
        opt.DefaultMaxDepth           = 1;      // 1-level-deep payload serialisation (default)
        opt.DefaultMaxContentLength   = 10_000;
        opt.SensitiveDataPolicy.SensitiveFields.Add("apiKey");
    },
    http: opt =>
    {
        opt.EnableRequestLogging  = true;
        opt.EnableResponseLogging = true;
        opt.ExcludePaths          = new[] { "/health", "/metrics", "/swagger" };
    });

var app = builder.Build();

app.UseDragonfireLogging();   // adds the middleware (needed for minimal APIs)
app.MapControllers();
app.Run();
```

---

## 4. Service-layer interception — runtime proxy

Mark any service class with `ILoggable`:

```csharp
using Dragonfire.Logging.Abstractions;

public class OrderService : IOrderService, ILoggable
{
    public async Task<Order> CreateOrderAsync(string tenantId, CreateOrderRequest request)
    {
        // ... real implementation
    }
}
```

With `EnableServiceInterception = true`, Dragonfire finds every registered service whose concrete implementation carries `ILoggable` and wraps it with a `DispatchProxy`-based logging proxy — **no code changes inside the service, no attributes required**.

### How the runtime proxy works

```
IOrderService (DI) ──resolves──► OrderServiceLoggingProxy (DispatchProxy)
                                        │
                                   ┌────▼────────────────────────────────┐
                                   │  1. Build LogEntry (service, method) │
                                   │  2. FilterData(args, maxDepth)       │
                                   │  3. Extract [LogProperty] fields     │
                                   │  4. Call _inner.Method(args)         │
                                   │  5. Capture result / exception       │
                                   │  6. LogAsync(entry) via ILogger      │
                                   └─────────────────────────────────────┘
```

The proxy uses `System.Reflection.DispatchProxy` — a **built-in .NET type**, no third-party AOP library involved.

---

## 5. Source generator — compile-time proxy

### Why the generator exists

The runtime `DispatchProxy` approach works well but has two costs:

| | Runtime proxy (`DispatchProxy`) | Compile-time proxy (generator) |
|---|---|---|
| **Reflection at startup** | Yes — `MakeGenericType`, `GetMethod` per service | None — concrete class exists at build time |
| **Reflection per call** | Yes — `MethodInfo.Invoke` | None — direct virtual dispatch |
| **Debuggability** | Stack frames show `DispatchProxy` internals | Stack frames show your actual proxy class |
| **AOT / NativeAOT** | ❌ Not compatible | ✅ Fully compatible |
| **Trimming** | ❌ Requires trim-unsafe reflection | ✅ Trim-safe |
| **IDE "go to definition"** | Goes to DispatchProxy | Goes to the generated `.g.cs` file |
| **Build-time visibility** | No | Generated file inspectable in `obj/Generated/` |

**Choose the generator** when you care about startup time, NativeAOT, trimming, or just want cleaner stack traces and full IDE navigation.

### Installation

**Via project reference** (monorepo / local development):

```xml
<!-- YourApi.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Dragonfire.Logging\Dragonfire.Logging.csproj" />

  <!-- Generator: OutputItemType="Analyzer" tells MSBuild to run it as a source generator.
       ReferenceOutputAssembly="false" means its DLL is NOT added to your app's references
       — the generator only runs at build time and produces .cs files, nothing more. -->
  <ProjectReference
      Include="..\Dragonfire.Logging.Generator\Dragonfire.Logging.Generator.csproj"
      OutputItemType="Analyzer"
      ReferenceOutputAssembly="false" />
</ItemGroup>
```

**Via NuGet** (published package):

```xml
<ItemGroup>
  <PackageReference Include="Dragonfire.Logging"           Version="1.0.0" />
  <PackageReference Include="Dragonfire.Logging.Generator" Version="1.0.0" />
</ItemGroup>
```

When installed from NuGet the generator DLL lives in `analyzers/dotnet/cs/` inside the package. MSBuild picks it up automatically — no `OutputItemType` needed.

> **Note:** Do not use `EnableServiceInterception = true` at the same time as the generator — they serve the same purpose. Pick one approach.

### How it works

At **build time**, the generator:

1. Scans every `class` declaration that has a non-empty base list
2. Checks if the class's semantic symbol implements `Dragonfire.Logging.Abstractions.ILoggable`
3. Collects all non-framework interfaces the class also implements (`IOrderService`, etc.)
4. Reads `[Log]`, `[LogIgnore]`, and `[LogProperty]` attributes from the Roslyn semantic model
5. Emits one `{ClassName}LoggingProxy.g.cs` per service **and** a shared `DragonfireGeneratedExtensions.g.cs`

The generator runs incrementally — only re-generates files for types whose syntax or attributes changed. Clean builds are fast; incremental builds are near-instant.

### What gets generated

Given this service:

```csharp
public interface IOrderService
{
    Task<Order> GetOrderAsync(
        [LogProperty("TenantId")] string tenantId,
        string orderId);

    [Log(MaxDepth = 0, Level = LogLevel.Debug)]
    void ProcessOrder(string orderId);

    [LogIgnore]
    string GetVersion();
}

public class OrderService : IOrderService, ILoggable
{
    public Task<Order> GetOrderAsync(string tenantId, string orderId) { ... }
    public void ProcessOrder(string orderId) { ... }
    public string GetVersion() => "1.0";
}
```

The generator emits `OrderServiceLoggingProxy.g.cs`:

```csharp
// <auto-generated/>
// Generated by Dragonfire.Logging.Generator
#nullable enable

namespace MyApp.Services.Generated
{
    [GeneratedCode("Dragonfire.Logging.Generator", "1.0.0")]
    [DebuggerNonUserCode]
    internal sealed class OrderServiceLoggingProxy : global::MyApp.Services.IOrderService
    {
        private readonly global::MyApp.Services.IOrderService _innerIOrderService;
        private readonly IDragonfireLoggingService _loggingService;
        private readonly ILogFilterService _filterService;
        private readonly DragonfireLoggingOptions _options;

        public OrderServiceLoggingProxy(
            global::MyApp.Services.IOrderService inner,
            IDragonfireLoggingService loggingService,
            ILogFilterService filterService,
            DragonfireLoggingOptions options) { ... }

        // ── GetOrderAsync — async Task<T>, [LogProperty] on first param ──────
        public async Task<Order> GetOrderAsync(string tenantId, string orderId)
        {
            var __entry = new LogEntry
            {
                ServiceName = "OrderService",
                MethodName  = "GetOrderAsync",
                Level       = _options.DefaultLogLevel,
            };

            // Arguments serialised at global DefaultMaxDepth (1 by default)
            __entry.MethodArguments = _filterService.FilterData(
                new object?[] { tenantId, orderId }, null, null,
                _options.DefaultMaxContentLength, _options.DefaultMaxDepth);

            // [LogProperty("TenantId")] captured directly — NO reflection
            __entry.NamedProperties ??= new Dictionary<string, object?>();
            __entry.NamedProperties.TryAdd("TenantId", tenantId);

            var __sw = Stopwatch.StartNew();
            Order __result = default!;
            try
            {
                __result = await _innerIOrderService.GetOrderAsync(tenantId, orderId)
                    .ConfigureAwait(false);

                __entry.MethodResult = _filterService.FilterData(
                    __result, null, null,
                    _options.DefaultMaxContentLength, _options.DefaultMaxDepth);
            }
            catch (Exception __ex)
            {
                __entry.IsError      = true;
                __entry.Level        = LogLevel.Error;
                __entry.ErrorMessage = __ex.Message;
                if (_options.IncludeStackTraceOnError)
                    __entry.StackTrace = __ex.StackTrace;
                throw;
            }
            finally
            {
                __sw.Stop();
                __entry.ElapsedMilliseconds = __sw.ElapsedMilliseconds;
                await _loggingService.LogAsync(__entry).ConfigureAwait(false);
            }

            return __result;
        }

        // ── ProcessOrder — [Log(MaxDepth = 0, Level = Debug)] ────────────────
        // MaxDepth = 0 emitted as a literal integer — resolved at compile time
        public void ProcessOrder(string orderId)
        {
            var __entry = new LogEntry { ..., Level = LogLevel.Debug };
            __entry.MethodArguments = _filterService.FilterData(
                new object?[] { orderId }, null, null,
                _options.DefaultMaxContentLength, 0 /* MaxDepth=0 = unlimited */);
            // ... try/catch/finally calling _loggingService.Log(__entry)
        }

        // ── GetVersion — [LogIgnore] → pure pass-through, zero overhead ──────
        public string GetVersion() => _innerIOrderService.GetVersion();
    }
}
```

Key observations:
- **No `MethodInfo.Invoke`** — all calls are direct virtual dispatch
- **No `MakeGenericType`** — generic Task dispatch resolved at compile time
- **`[LogProperty]` captured with a literal assignment** — `TryAdd("TenantId", tenantId)` — no reflection, no attribute scanning at runtime
- **`[Log(MaxDepth = 0)]`** emits `0` as a literal — the attribute is consumed entirely at build time
- **`[LogIgnore]`** produces a one-liner pass-through — method excluded from all logging infrastructure

### DI registration

```csharp
// Program.cs

// 1. Register services normally
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IInventoryService, InventoryService>(); // also : ILoggable

// 2. Add core Dragonfire (but do NOT set EnableServiceInterception — the generator handles wrapping)
builder.Services.AddDragonfireLogging(opt =>
{
    opt.DefaultMaxDepth         = 1;
    opt.DefaultMaxContentLength = 10_000;
});

// 3. Wrap all ILoggable services with their generated proxies
//    This single call replaces every ILoggable service registration with its proxy.
builder.Services.AddDragonfireGeneratedLogging();
```

`AddDragonfireGeneratedLogging()` is itself **generated** — it contains one `DecorateService(...)` call per discovered `ILoggable` class. It includes its own inline `DecorateService`/`ResolveInner` helpers; no Scrutor or other decoration library is needed.

### Inspect the generated files

Enable `EmitCompilerGeneratedFiles` to write the `.g.cs` files to disk:

```xml
<!-- YourApi.csproj -->
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Files appear at:
```
obj/Debug/net8.0/Generated/
  Dragonfire.Logging.Generator/
    Dragonfire.Logging.Generator.LoggingProxyGenerator/
      OrderServiceLoggingProxy.g.cs
      DragonfireGeneratedExtensions.g.cs
```

---

## 6. Structured logging and Application Insights

Every log entry is written via `ILogger.BeginScope(Dictionary<string, object>)` with individually named properties. **Each field gets its own key** — nothing is buried in a single serialised JSON string.

### Properties emitted for every entry

| Scope key | Example value | When set |
|---|---|---|
| `Dragonfire.CorrelationId` | `"d4f2…"` | Always |
| `Dragonfire.TraceId` | `"00-4b3a…"` | When `Activity.Current` is set |
| `Dragonfire.ServiceName` | `"OrderService"` | Service-layer entries |
| `Dragonfire.MethodName` | `"CreateOrderAsync"` | Service-layer entries |
| `Dragonfire.HttpMethod` | `"POST"` | HTTP entries |
| `Dragonfire.Path` | `"/api/orders"` | HTTP entries |
| `Dragonfire.StatusCode` | `200` | HTTP entries |
| `Dragonfire.ElapsedMs` | `42` | Always |
| `Dragonfire.IsError` | `true` | Only on errors |
| `Dragonfire.ErrorMessage` | `"Object not found"` | Only on errors |
| `Dragonfire.StackTrace` | `"at …"` | Errors, when enabled |
| `Dragonfire.RequestData` | `{"orderId":"x"}` | Serialised payload (filtered) |
| `Dragonfire.ResponseData` | `{"status":"ok"}` | Serialised payload (filtered) |
| `Dragonfire.Custom.{Key}` | any | From `AddCustomData()` |
| `{YourKey}` | `"acme"` | `[LogProperty]`-promoted fields |

### KQL queries in Application Insights

```kql
// All failed calls to any service in the last hour
traces
| where timestamp > ago(1h)
| where customDimensions["Dragonfire.IsError"] == "True"
| project timestamp, customDimensions["Dragonfire.ServiceName"],
          customDimensions["Dragonfire.MethodName"],
          customDimensions["Dragonfire.ErrorMessage"]

// P95 latency per method
traces
| where isnotempty(customDimensions["Dragonfire.ElapsedMs"])
| summarize percentile(toint(customDimensions["Dragonfire.ElapsedMs"]), 95)
    by tostring(customDimensions["Dragonfire.MethodName"])

// All orders for a specific tenant (via [LogProperty("TenantId")])
traces
| where customDimensions["TenantId"] == "acme-corp"
| project timestamp, customDimensions["Dragonfire.MethodName"],
          customDimensions["Dragonfire.ElapsedMs"]
```

---

## 7. [LogProperty] — promote fields to first-class dimensions

By default, method arguments and return values are serialised as a JSON snapshot in `Dragonfire.RequestData` / `Dragonfire.MethodArguments`. Filtering for a specific tenant requires parsing that JSON string.

`[LogProperty]` solves this: it promotes a **specific value** to its own named scope key that appears directly in `customDimensions` — no JSON parsing, directly queryable.

### On a method parameter

```csharp
public async Task<Order> CreateOrderAsync(
    [LogProperty("TenantId")] string tenantId,
    [LogProperty]             string customerId,   // key = "customerId"
    CreateOrderRequest        request)             // not promoted individually
{ ... }
```

### On a DTO property

```csharp
public class CreateOrderRequest
{
    [LogProperty("OrderRef")]   // key = "OrderRef" in customDimensions
    public string ExternalReference { get; set; }

    [LogProperty]               // key = "Region"
    public string Region { get; set; }

    public List<OrderLineDto> Lines { get; set; }   // not promoted
}
```

Both are supported at the same time. The runtime proxy uses `ILogFilterService.ExtractNamedProperties()` for DTO properties; the **source generator emits the DTO extraction as a direct `_filterService.ExtractNamedProperties(request)` call, and captures parameter-level `[LogProperty]` with a simple field assignment** — no reflection involved at the parameter level.

### Result in Application Insights

```kql
// Before [LogProperty] — must parse JSON
traces
| where customDimensions["Dragonfire.MethodArguments"] contains "acme-corp"

// After [LogProperty] — direct indexed lookup, 10-100× faster
traces
| where customDimensions["TenantId"] == "acme-corp"
```

---

## 8. Payload depth control

By default, request/response/argument payloads are serialised **1 level deep**. Nested objects beyond that depth are replaced with a compact placeholder:

```json
{
  "orderId": "ORD-123",
  "customer": "[3 fields]",
  "lines":    "[5 items]"
}
```

This keeps logs concise and avoids accidentally logging deeply nested sensitive structures.

### Opt out globally

```csharp
builder.Services.AddDragonfireLogging(opt =>
{
    opt.DefaultMaxDepth = 0;  // 0 = unlimited — full serialisation
});
```

### Opt out per method

```csharp
[Log(MaxDepth = 0)]   // unlimited for this action/method only
public async Task<OrderDetail> GetOrderDetailAsync(string id) { ... }

[Log(MaxDepth = 3)]   // 3 levels deep for this method
public async Task<Report> GenerateReportAsync(ReportRequest req) { ... }
```

### Depth semantics

| `MaxDepth` value | Behaviour |
|---|---|
| `0` | Unlimited — full serialisation |
| `1` (default) | Top-level scalar properties only; nested objects/arrays → `[N fields]` / `[N items]` |
| `N` | N levels of nesting preserved |
| `-1` (on `[Log]`) | Use `DragonfireLoggingOptions.DefaultMaxDepth` |

The **source generator resolves `[Log(MaxDepth)]` at compile time** and emits the resolved integer as a literal — no options lookup at runtime for the depth value.

---

## 9. [Log] attribute reference

Apply to a class (default for all members) or a method (overrides class-level):

```csharp
[Log(
    LogRequest        = true,          // log arguments / request body
    LogResponse       = true,          // log return value / response body
    LogValidationErrors = true,        // include ModelState errors (ASP.NET Core)
    Level             = LogLevel.Information,
    MaxContentLength  = 5_000,         // truncate payloads to 5 KB (0 = unlimited)
    MaxDepth          = 1,             // nesting depth (-1 = use global default)
    ExcludeProperties = new[] { "CardNumber", "Cvv" },
    IncludeProperties = new[] { },     // when non-empty, ONLY these properties are kept
    LogHeaders        = false,         // include HTTP request headers
    ExcludeHeaders    = new[] { "Authorization", "Cookie", "X-API-Key" },
    CustomContext     = "payment-flow" // free-text annotation in every log entry
)]
```

### [LogIgnore]

```csharp
// On a property — removed from every serialised payload
public class UserDto
{
    [LogIgnore("PCI compliance")]
    public string CardNumber { get; set; }
}

// On a method — proxy passes through with ZERO logging overhead
[LogIgnore]
public string GetHealthStatus() => "ok";

// On a class — disables logging for all methods
[LogIgnore]
public class DiagnosticsService : IDiagnosticsService, ILoggable { ... }
```

---

## 10. Sensitive data redaction

Redaction runs on every serialised payload, applied **after** property filtering.

```csharp
builder.Services.AddDragonfireLogging(opt =>
{
    var policy = opt.SensitiveDataPolicy;

    // Field names — properties are removed entirely
    policy.SensitiveFields.Add("ssn");
    policy.SensitiveFields.Add("privateKey");

    // Regex patterns — matched content replaced
    policy.RedactionPatterns.Add((
        new Regex(@"\b4[0-9]{12}(?:[0-9]{3})?\b"),  // Visa card numbers
        "[CARD_REDACTED]"));

    // Built-in toggles
    policy.RedactEmails      = true;   // default: true
    policy.RedactPhoneNumbers = true;  // default: true
});
```

### Built-in redactions (always active)

| Category | Pattern | Replacement |
|---|---|---|
| Sensitive field names | `password`, `secret`, `token`, `authorization`, `cvv`, `ssn` | Property removed |
| Credit card numbers | Luhn-range regex | `[CREDIT_CARD_REDACTED]` |
| SSNs | `NNN-NN-NNNN` | `[SSN_REDACTED]` |
| JWT Bearer tokens | `Bearer eyJ…` | `Bearer [JWT_REDACTED]` |
| Email addresses | RFC 5322 pattern | `[EMAIL_REDACTED]` |
| Phone numbers | `NNN-NNN-NNNN` variants | `[PHONE_REDACTED]` |

---

## 11. HTTP logging (ASP.NET Core)

`Dragonfire.Logging.AspNetCore` provides two complementary hooks:

### MVC / Web API controllers — `DragonfireLoggingFilter`

Registered as a global `IAsyncActionFilter` at order `int.MinValue` (outermost). Reads the response via `ObjectResult.Value` — **the response body stream is never replaced or hijacked**.

Supports `[Log]` and `[LogIgnore]` on controllers and actions.

### Minimal APIs — `DragonfireLoggingMiddleware`

```csharp
app.UseDragonfireLogging();  // must be called before app.MapGet/MapPost/...
```

### Correlation ID propagation

Every request gets a `X-Correlation-ID` header echoed back in the response. If the caller sends one it is reused; otherwise a new GUID is generated. The same ID appears in every log entry for that request.

### Excluding paths

```csharp
http: opt => opt.ExcludePaths = new[] { "/health", "/metrics", "/swagger", "/favicon.ico" }
```

---

## 12. Configuration reference

### `DragonfireLoggingOptions` (core)

| Property | Default | Description |
|---|---|---|
| `EnableServiceInterception` | `false` | Auto-wrap `ILoggable` services with the runtime `DispatchProxy`. Set to `false` when using the source generator. |
| `DefaultMaxDepth` | `1` | Default payload nesting depth. `0` = unlimited. |
| `DefaultMaxContentLength` | `10 000` | Truncate serialised payloads to this many characters. `0` = unlimited. |
| `IncludeStackTraceOnError` | `true` | Append `StackTrace` to error log entries. |
| `DefaultLogLevel` | `Information` | Level used when no `[Log]` attribute is present. |
| `SensitiveDataPolicy` | (see above) | Redaction rules applied to all payloads. |
| `CustomLogAction` | `null` | Optional `Action<LogEntry>` callback — forward entries to a custom sink after the standard `ILogger` write. |
| `LoggingServiceLifetime` | `Scoped` | DI lifetime for `IDragonfireLoggingService`. |

### `DragonfireAspNetCoreOptions` (ASP.NET Core)

| Property | Default | Description |
|---|---|---|
| `EnableRequestLogging` | `true` | Log inbound request arguments and body. |
| `EnableResponseLogging` | `true` | Log outbound response body (from `ObjectResult.Value`). |
| `LogValidationErrors` | `true` | Include `ModelState` errors in the log entry. |
| `ExcludePaths` | `[]` | Path prefixes to skip entirely (health checks, swagger, etc.). |
| `CaptureResponseBodyInMiddleware` | `false` | Opt-in stream capture for minimal API response bodies. |

---

## 13. Requirements

| | Version |
|---|---|
| .NET | 8.0+ |
| ASP.NET Core | 8.0+ (via `Microsoft.AspNetCore.App` framework reference) |
| Roslyn (for generator) | 4.8+ (shipped with .NET 8 SDK) |

**NuGet dependencies** (core package only):

```
Microsoft.Extensions.Logging.Abstractions            8.0.2
Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2
Newtonsoft.Json                                      13.0.3
```

The `AspNetCore` package adds zero NuGet dependencies beyond a `FrameworkReference`. The `Generator` package adds zero runtime dependencies — `Microsoft.CodeAnalysis.CSharp` is `PrivateAssets="all"` and never flows to the consuming project.

---

## License

MIT
