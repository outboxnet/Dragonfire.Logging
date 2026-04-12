# Dragonfire.Logging

Production-ready, zero-boilerplate **structured logging** for .NET 8+ services and ASP.NET Core APIs.

```
Dragonfire.Logging              — core (framework-agnostic)
Dragonfire.Logging.AspNetCore   — HTTP request/response logging for MVC controllers
Dragonfire.Logging.Generator    — Roslyn source generator (compile-time proxies, no runtime overhead)
```

Every log entry writes **individually named structured properties** — not one opaque JSON blob — so you can filter, alert, and build dashboards directly against each field in Application Insights, Seq, Loki, Datadog, or any `ILogger`-compatible sink.

---

## Table of contents

1. [What problem does this solve?](#1-what-problem-does-this-solve)
2. [Package overview](#2-package-overview)
3. [Source generator — service-layer logging](#3-source-generator--service-layer-logging)
   - [How it works](#how-it-works)
   - [Installation](#installation)
   - [Annotate your services](#annotate-your-services)
   - [DI registration and runtime options](#di-registration-and-runtime-options)
   - [What gets generated](#what-gets-generated)
   - [customDimensions produced](#customdimensions-produced)
   - [Inspect generated files locally](#inspect-generated-files-locally)
   - [Verify locally without Application Insights](#verify-locally-without-application-insights)
4. [ASP.NET MVC — controller request/response logging](#4-aspnet-mvc--controller-requestresponse-logging)
   - [How it works](#how-it-works-1)
   - [Installation](#installation-1)
   - [Annotate your controllers](#annotate-your-controllers)
   - [[LogProperty] on action parameters and response DTOs](#logproperty-on-action-parameters-and-response-dtos)
   - [customDimensions produced](#customdimensions-produced-1)
5. [[LogProperty] reference](#5-logproperty-reference)
6. [[Log] attribute reference](#6-log-attribute-reference)
7. [Runtime options reference](#7-runtime-options-reference)
8. [KQL queries in Application Insights](#8-kql-queries-in-application-insights)
9. [Requirements](#9-requirements)

---

## 1. What problem does this solve?

Writing logging code by hand is tedious, inconsistent, and almost always incomplete:

```csharp
// What every team ends up with
public async Task<Order> CreateOrderAsync(string tenantId, CreateOrderRequest req)
{
    _logger.LogInformation("CreateOrder called tenantId={TenantId}", tenantId);
    var sw = Stopwatch.StartNew();
    try
    {
        var result = await _db.InsertAsync(req);
        _logger.LogInformation("CreateOrder succeeded in {Ms}ms", sw.ElapsedMilliseconds);
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "CreateOrder failed");  // no elapsed time, no context
        throw;
    }
}
```

**Dragonfire.Logging automates this at compile time.** The Roslyn source generator reads your attributes at build time and emits a concrete proxy class that:

- Times every call with sub-millisecond precision
- Logs success and failure with a consistent structured format
- Promotes `[LogProperty]`-annotated fields directly to `customDimensions` — no JSON parsing in KQL
- Uses only `ILogger<T>` — zero runtime reflection, zero third-party dependencies
- Is fully AOT/trim-safe and debuggable (stack traces point to the generated `.g.cs` file)

---

## 2. Package overview

| Package | Target | Purpose |
|---|---|---|
| `Dragonfire.Logging` | `net8.0` | Core: `ILoggable`, `[Log]`, `[LogProperty]`, `[LogIgnore]` attributes |
| `Dragonfire.Logging.AspNetCore` | `net8.0` | MVC action filter for controller request/response logging |
| `Dragonfire.Logging.Generator` | `netstandard2.0` (analyzer) | Roslyn source generator — emits compile-time proxy classes |

---

## 3. Source generator — service-layer logging

### How it works

At **build time**, the generator:

1. Finds every `class` that implements `ILoggable` (directly or via an interface)
2. Collects the service interfaces that class also implements (`IOrderService`, etc.)
3. Reads `[Log]`, `[LogIgnore]`, and `[LogProperty]` attributes from the Roslyn semantic model — no runtime attribute scanning
4. Emits one concrete proxy class per service, plus shared helpers

The generated proxy uses only `ILogger<T>` and a small generated options class. It has **no dependency on any Dragonfire runtime service** — the generator is self-contained.

| | Runtime proxy (`DispatchProxy`) | Compile-time proxy (generator) |
|---|---|---|
| Reflection at startup | Yes | None |
| Reflection per call | Yes (`MethodInfo.Invoke`) | None — direct virtual dispatch |
| AOT / NativeAOT | ❌ | ✅ |
| Trimming | ❌ | ✅ |
| Debuggability | `DispatchProxy` internals in stack trace | Real `.g.cs` file, full IDE navigation |
| Build-time visibility | None | Inspectable in `obj/Generated/` |

### Installation

**Project reference** (monorepo / local development):

```xml
<!-- YourApi.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Dragonfire.Logging\Dragonfire.Logging.csproj" />

  <!-- ReferenceOutputAssembly="false" — the generator only runs at build time,
       its DLL is never added to your app's dependencies. -->
  <ProjectReference
      Include="..\Dragonfire.Logging.Generator\Dragonfire.Logging.Generator.csproj"
      OutputItemType="Analyzer"
      ReferenceOutputAssembly="false" />
</ItemGroup>
```

**NuGet** (published package):

```xml
<ItemGroup>
  <PackageReference Include="Dragonfire.Logging"           Version="1.0.0" />
  <PackageReference Include="Dragonfire.Logging.Generator" Version="1.0.0" />
</ItemGroup>
```

### Annotate your services

`ILoggable` can live on the **interface** or the **class** — both patterns work:

```csharp
using Dragonfire.Logging.Abstractions;
using Dragonfire.Logging.Attributes;
using Microsoft.Extensions.Logging;

// ── Pattern A: ILoggable on the interface ────────────────────────────────────
// Attributes live on the interface. The class needs no logging attributes at all.

public interface IOrderService : ILoggable
{
    [Log]
    Task<Order> GetOrderAsync(
        [LogProperty("TenantId")] string tenantId,   // → customDimensions["Request.TenantId"]
        [LogProperty]             string orderId);    // → customDimensions["Request.orderId"]

    [Log(Level = LogLevel.Debug)]
    void ProcessOrder(string orderId);

    [LogIgnore]                    // pass-through, zero logging overhead
    string GetVersion();
}

public class OrderService : IOrderService   // no ILoggable here — inherited via interface
{
    public Task<Order> GetOrderAsync(string tenantId, string orderId) { ... }
    public void ProcessOrder(string orderId) { ... }
    public string GetVersion() => "1.0";
}

// ── Pattern B: ILoggable on the class ────────────────────────────────────────
// Attributes can be on the interface, the class, or both.

public interface IInventoryService
{
    Task<int> GetStockAsync([LogProperty("Sku")] string sku);
}

public class InventoryService : IInventoryService, ILoggable
{
    public Task<int> GetStockAsync(string sku) => Task.FromResult(42);
}
```

### [LogProperty] on DTO types

Mark properties on request/response DTOs to promote them individually to `customDimensions`:

```csharp
public class CreateOrderRequest
{
    [LogProperty("OrderRef")]   // → customDimensions["Request.OrderRef"]
    public string ExternalReference { get; set; }

    [LogProperty]               // → customDimensions["Request.Region"]
    public string Region { get; set; }

    public List<OrderLineDto> Lines { get; set; }  // not promoted — not annotated
}

public class Order
{
    [LogProperty]               // → customDimensions["Response.OrderId"]
    public string OrderId { get; set; }

    [LogProperty("Price")]      // → customDimensions["Response.Price"]
    public decimal TotalAmount { get; set; }
}
```

The generator reads these annotations at **build time** and emits direct property reads — `__scope["Response.Price"] = __result.TotalAmount;` — no runtime reflection.

### DI registration and runtime options

```csharp
// Program.cs

// 1. Register your services normally
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();

// 2. Wrap all ILoggable services with their generated proxies
//    The configure lambda is optional — omit it to use all defaults.
builder.Services.AddDragonfireGeneratedLogging(options =>
{
    options.LogRequestProperties  = true;   // include Request.* fields (default: true)
    options.LogResponseProperties = true;   // include Response.* fields (default: true)
    options.LogStackTrace         = false;  // omit Dragonfire.StackTrace (default: true)
    options.OverrideLevel         = null;   // null = use [Log(Level=...)] per method

    // Suppress specific fields by bare name (matches both Request.X and Response.X)
    options.ExcludeProperties.Add("InternalId");
    options.ExcludeProperties.Add("RawPayload");
});
```

`AddDragonfireGeneratedLogging()` is itself **generated** — it contains exactly one `DecorateService` call per discovered `ILoggable` class. No Scrutor or decoration library is needed.

### What gets generated

For the `IOrderService` above, the generator emits `OrderServiceLoggingProxy.g.cs`:

```csharp
// <auto-generated/>  — lives in obj/Generated/ at build time
internal sealed class OrderServiceLoggingProxy : global::MyApp.Services.IOrderService
{
    private readonly global::MyApp.Services.IOrderService _innerIOrderService;
    private readonly global::Microsoft.Extensions.Logging.ILogger<global::MyApp.Services.OrderService> _logger;
    private readonly global::Dragonfire.Logging.Generated.DragonfireGeneratedLoggingOptions _options;

    public async global::System.Threading.Tasks.Task<global::MyApp.Services.Order> GetOrderAsync(
        string tenantId, string orderId)
    {
        var __scope = new global::Dragonfire.Logging.Generated.__DragonfireScopeState(
            "[Dragonfire] OrderService.GetOrderAsync");
        __scope["Dragonfire.ServiceName"] = "OrderService";
        __scope["Dragonfire.MethodName"]  = "GetOrderAsync";

        // Request fields — guarded by options, resolved at compile time, zero reflection
        if (_options.LogRequestProperties)
        {
            // [LogProperty("TenantId")] on parameter 'tenantId'
            if (!_options.IsExcluded("TenantId"))
                __scope["Request.TenantId"] = tenantId;

            // [LogProperty] on parameter 'orderId'
            if (!_options.IsExcluded("orderId"))
                __scope["Request.orderId"] = orderId;
        }

        global::MyApp.Services.Order __result = default!;
        var __sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            __result = await _innerIOrderService.GetOrderAsync(tenantId, orderId)
                .ConfigureAwait(false);

            // Response fields — from [LogProperty] on Order properties
            if (_options.LogResponseProperties && __result is not null)
            {
                // [LogProperty] on result.OrderId
                if (!_options.IsExcluded("OrderId"))
                    __scope["Response.OrderId"] = __result.OrderId;

                // [LogProperty("Price")] on result.TotalAmount
                if (!_options.IsExcluded("Price"))
                    __scope["Response.Price"] = __result.TotalAmount;
            }

            __sw.Stop();
            __scope["Dragonfire.ElapsedMs"] = __sw.Elapsed.TotalMilliseconds;
            using (_logger.BeginScope(__scope))
                _logger.Log(
                    _options.OverrideLevel ?? global::Microsoft.Extensions.Logging.LogLevel.Information,
                    "[Dragonfire] {Dragonfire_ServiceName}.{Dragonfire_MethodName} completed in {Dragonfire_ElapsedMs}ms",
                    "OrderService", "GetOrderAsync", __sw.Elapsed.TotalMilliseconds);
        }
        catch (global::System.Exception __ex)
        {
            __sw.Stop();
            __scope["Dragonfire.ElapsedMs"]    = __sw.Elapsed.TotalMilliseconds;
            __scope["Dragonfire.IsError"]      = true;
            __scope["Dragonfire.ErrorMessage"] = __ex.Message;
            if (_options.LogStackTrace)
                __scope["Dragonfire.StackTrace"] = __ex.StackTrace;
            using (_logger.BeginScope(__scope))
                _logger.Log(
                    _options.OverrideLevel ?? global::Microsoft.Extensions.Logging.LogLevel.Error,
                    __ex,
                    "[Dragonfire] {Dragonfire_ServiceName}.{Dragonfire_MethodName} FAILED in {Dragonfire_ElapsedMs}ms — {Dragonfire_ErrorMessage}",
                    "OrderService", "GetOrderAsync", __sw.Elapsed.TotalMilliseconds, __ex.Message);
            throw;
        }
        return __result;
    }

    // [LogIgnore] → pure pass-through, zero logging overhead
    public string GetVersion() => _innerIOrderService.GetVersion();
}
```

Key properties:
- **No `MethodInfo.Invoke`** — all calls are direct virtual dispatch
- **No JSON serialisation** — only explicitly annotated `[LogProperty]` fields are captured
- **No runtime reflection** — DTO property reads are direct C# property accesses, resolved at build time
- **`[LogIgnore]`** produces a one-liner — zero overhead

### customDimensions produced

For a successful `GetOrderAsync` call:

```json
{
  "Message":                  "[Dragonfire] OrderService.GetOrderAsync",
  "Dragonfire.ServiceName":   "OrderService",
  "Dragonfire.MethodName":    "GetOrderAsync",
  "Request.TenantId":         "acme-corp",
  "Request.orderId":          "ORD-123",
  "Response.OrderId":         "ORD-123",
  "Response.Price":           49.99,
  "Dragonfire.ElapsedMs":     1.847
}
```

On error, the scope additionally includes:

```json
{
  "Dragonfire.IsError":       true,
  "Dragonfire.ErrorMessage":  "Value cannot be null.",
  "Dragonfire.StackTrace":    "   at OrderService.GetOrderAsync ..."
}
```

### Inspect generated files locally

Add to your `.csproj` to write `.g.cs` files to disk during build:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Files land at:

```
obj/Debug/net8.0/Generated/
  Dragonfire.Logging.Generator/
    Dragonfire.Logging.Generator.LoggingProxyGenerator/
      OrderServiceLoggingProxy.g.cs
      InventoryServiceLoggingProxy.g.cs
      DragonfireGeneratedExtensions.g.cs       ← AddDragonfireGeneratedLogging()
      DragonfireGeneratedLoggingOptions.g.cs   ← runtime options class
      DragonfireGeneratedScopeState.g.cs       ← scope wrapper
```

### Verify locally without Application Insights

The default console logger drops scope data. Use the JSON console formatter to see every `customDimension` locally:

```csharp
// Program.cs — development only
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes     = true;
    o.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = true };
});
```

Or in `appsettings.Development.json` (no code change needed):

```json
{
  "Logging": {
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "IncludeScopes": true,
        "JsonWriterOptions": { "Indented": true }
      }
    }
  }
}
```

For a full local UI with search and filters — identical to Application Insights — run Seq in Docker:

```bash
docker run --name seq -d -e ACCEPT_EULA=Y -p 5341:5341 -p 80:80 datalust/seq
```

```bash
dotnet add package Seq.Extensions.Logging
```

```csharp
builder.Logging.AddSeq("http://localhost:5341");
```

Open `http://localhost` — every scope property is a searchable field.

---

## 4. ASP.NET MVC — controller request/response logging

### How it works

`Dragonfire.Logging.AspNetCore` registers a global `IAsyncActionFilter` (`DragonfireLoggingFilter`) that runs at `int.MinValue` (outermost filter position). It:

1. Captures action **parameters** before execution — reads them directly from the action's argument dictionary
2. Executes the inner pipeline
3. Reads the **response** from `ObjectResult.Value` — the response stream is never replaced or buffered
4. Writes a structured log entry via `ILogger.BeginScope` with the same `customDimensions` schema as the service generator

`[Log]`, `[LogProperty]`, and `[LogIgnore]` work on **controllers and actions** the same way they work on services.

### Installation

```xml
<!-- YourApi.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Dragonfire.Logging.AspNetCore\Dragonfire.Logging.AspNetCore.csproj" />
  <!-- or: <PackageReference Include="Dragonfire.Logging.AspNetCore" Version="1.0.0" /> -->
</ItemGroup>
```

```csharp
// Program.cs
builder.Services.AddControllers();
builder.Services.AddDragonfireAspNetCore(
    core: opt =>
    {
        opt.DefaultMaxDepth         = 1;
        opt.DefaultMaxContentLength = 10_000;
        opt.IncludeStackTraceOnError = true;
    },
    http: opt =>
    {
        opt.EnableRequestLogging  = true;
        opt.EnableResponseLogging = true;
        opt.ExcludePaths          = new[] { "/health", "/metrics", "/swagger" };
    });

var app = builder.Build();
app.UseDragonfireLogging();  // middleware for minimal APIs; safe to include for controllers too
app.MapControllers();
app.Run();
```

### Annotate your controllers

```csharp
using Dragonfire.Logging.Attributes;
using Microsoft.Extensions.Logging;

// [Log] on the controller sets defaults for all actions.
// Individual actions can override or opt out.
[ApiController]
[Route("api/[controller]")]
[Log(Level = LogLevel.Information)]
public class OrdersController : ControllerBase
{
    // Uses controller-level [Log] — logs request parameters and ObjectResult response
    [HttpGet("{orderId}")]
    public async Task<ActionResult<Order>> GetOrder(
        [LogProperty("TenantId")] string tenantId,   // → customDimensions["Request.TenantId"]
        string orderId)                               // not annotated — not promoted
    {
        var order = await _orderService.GetOrderAsync(tenantId, orderId);
        return Ok(order);
    }

    // Override log level per action
    [HttpPost]
    [Log(Level = LogLevel.Warning)]
    public async Task<ActionResult<Order>> CreateOrder(
        [LogProperty("TenantId")] string tenantId,
        [FromBody] CreateOrderRequest request)
    {
        var order = await _orderService.CreateOrderAsync(tenantId, request);
        return Created($"/api/orders/{order.OrderId}", order);
    }

    // Exempt sensitive actions from logging entirely
    [HttpDelete("{orderId}")]
    [LogIgnore]
    public async Task<IActionResult> DeleteOrder(string orderId)
    {
        await _orderService.DeleteOrderAsync(orderId);
        return NoContent();
    }
}
```

### [LogProperty] on action parameters and response DTOs

Works identically to the service generator:

```csharp
// Request DTO — annotated properties surface as Request.* in customDimensions
public class CreateOrderRequest
{
    [LogProperty("OrderRef")]   // → customDimensions["Request.OrderRef"]
    public string ExternalReference { get; set; }

    [LogProperty]               // → customDimensions["Request.Region"]
    public string Region { get; set; }

    public string InternalNotes { get; set; }  // not promoted
}

// Response DTO — annotated properties surface as Response.* in customDimensions
public class Order
{
    [LogProperty]               // → customDimensions["Response.OrderId"]
    public string OrderId { get; set; }

    [LogProperty("Price")]      // → customDimensions["Response.Price"]
    public decimal TotalAmount { get; set; }

    public string InternalState { get; set; }  // not promoted
}
```

### customDimensions produced

For a successful `POST /api/orders` call:

```json
{
  "Dragonfire.ServiceName":   "OrdersController",
  "Dragonfire.MethodName":    "CreateOrder",
  "Dragonfire.HttpMethod":    "POST",
  "Dragonfire.Path":          "/api/orders",
  "Dragonfire.StatusCode":    201,
  "Dragonfire.ElapsedMs":     23.441,
  "Request.TenantId":         "acme-corp",
  "Request.OrderRef":         "EXT-9981",
  "Request.Region":           "EU-WEST",
  "Response.OrderId":         "ORD-456",
  "Response.Price":           149.99
}
```

On a 500 error:

```json
{
  "Dragonfire.IsError":       true,
  "Dragonfire.StatusCode":    500,
  "Dragonfire.ErrorMessage":  "Database timeout",
  "Dragonfire.StackTrace":    "   at OrderService.CreateOrderAsync ..."
}
```

### Excluding paths

```csharp
http: opt => opt.ExcludePaths = new[] { "/health", "/metrics", "/swagger", "/favicon.ico" }
```

### Correlation ID

Every request gets a `X-Correlation-ID` header echoed back in the response. If the caller sends one it is reused; otherwise a new GUID is generated and appears in every log entry for that request.

---

## 5. [LogProperty] reference

| Usage | Syntax | customDimensions key |
|---|---|---|
| Parameter — named | `[LogProperty("TenantId")] string tenantId` | `Request.TenantId` |
| Parameter — use param name | `[LogProperty] string orderId` | `Request.orderId` |
| DTO property — named | `[LogProperty("OrderRef")] public string Ext { get; set; }` | `Request.OrderRef` (when on a param type) or `Response.OrderRef` (when on a return type) |
| DTO property — use prop name | `[LogProperty] public string Region { get; set; }` | `Request.Region` / `Response.Region` |

`[LogProperty]` can go on:
- Service interface method parameters (picked up by the source generator)
- Service class method parameters
- Controller action parameters (picked up by the ASP.NET Core filter)
- Properties of any DTO class that appears as a parameter or return type

The `Request.` / `Response.` prefix is applied automatically — you never write it in the attribute.

---

## 6. [Log] attribute reference

Apply to a class (default for all members) or a method (overrides class-level default):

```csharp
[Log(
    Level    = LogLevel.Information,  // log level for success path
    MaxDepth = 1                      // reserved for future payload capture; currently unused by generator
)]
```

### [LogIgnore]

```csharp
// On a method — proxy passes through with ZERO logging overhead
[LogIgnore]
public string GetHealthStatus() => "ok";

// On a class — disables logging for all methods on this service
[LogIgnore]
public class DiagnosticsService : IDiagnosticsService, ILoggable { ... }

// On a property — excluded from [LogProperty] DTO scanning
public class OrderRequest
{
    [LogIgnore]
    public string InternalAuditCode { get; set; }
}
```

---

## 7. Runtime options reference

`DragonfireGeneratedLoggingOptions` is itself generated — it lives in your assembly alongside the proxy classes. Configure it at registration time:

```csharp
builder.Services.AddDragonfireGeneratedLogging(options =>
{
    options.LogRequestProperties  = true;
    options.LogResponseProperties = true;
    options.LogStackTrace         = true;
    options.OverrideLevel         = null;
    options.ExcludeProperties.Add("SensitiveField");
});
```

| Option | Type | Default | Description |
|---|---|---|---|
| `LogRequestProperties` | `bool` | `true` | Include all `Request.*` scope entries (from `[LogProperty]` on parameters and their DTO types) |
| `LogResponseProperties` | `bool` | `true` | Include all `Response.*` scope entries (from `[LogProperty]` on the return type's properties) |
| `LogStackTrace` | `bool` | `true` | Include `Dragonfire.StackTrace` in error scope. Disable to reduce log size in production |
| `ExcludeProperties` | `ISet<string>` | empty | Bare property names to suppress (case-insensitive). `"OrderId"` suppresses both `Request.OrderId` and `Response.OrderId` |
| `OverrideLevel` | `LogLevel?` | `null` | Override the success log level for all methods. `null` = use the `[Log(Level = ...)]` attribute value. Error logs always use `LogLevel.Error` regardless of this setting |

---

## 8. KQL queries in Application Insights

```kql
// All failed service calls in the last hour
traces
| where timestamp > ago(1h)
| where customDimensions["Dragonfire.IsError"] == "True"
| project timestamp,
          customDimensions["Dragonfire.ServiceName"],
          customDimensions["Dragonfire.MethodName"],
          customDimensions["Dragonfire.ErrorMessage"],
          customDimensions["Dragonfire.ElapsedMs"]
| order by timestamp desc

// P95 latency per method
traces
| where isnotempty(customDimensions["Dragonfire.ElapsedMs"])
| summarize p95 = percentile(todouble(customDimensions["Dragonfire.ElapsedMs"]), 95)
    by tostring(customDimensions["Dragonfire.MethodName"])
| order by p95 desc

// All orders for a specific tenant — direct indexed lookup, no JSON parsing
traces
| where customDimensions["Request.TenantId"] == "acme-corp"
| project timestamp,
          customDimensions["Dragonfire.MethodName"],
          customDimensions["Request.orderId"],
          customDimensions["Response.Price"],
          customDimensions["Dragonfire.ElapsedMs"]

// HTTP 500 errors on a specific controller action
traces
| where customDimensions["Dragonfire.StatusCode"] == "500"
    and customDimensions["Dragonfire.MethodName"] == "CreateOrder"
| project timestamp,
          customDimensions["Request.TenantId"],
          customDimensions["Request.OrderRef"],
          customDimensions["Dragonfire.ErrorMessage"]
| order by timestamp desc

// Slow requests (> 500 ms) across all services and controllers
traces
| where todouble(customDimensions["Dragonfire.ElapsedMs"]) > 500
| project timestamp,
          customDimensions["Dragonfire.ServiceName"],
          customDimensions["Dragonfire.MethodName"],
          customDimensions["Dragonfire.ElapsedMs"]
| order by todouble(customDimensions["Dragonfire.ElapsedMs"]) desc
```

---

## 9. Requirements

| | Version |
|---|---|
| .NET | 8.0+ |
| ASP.NET Core | 8.0+ (`Dragonfire.Logging.AspNetCore` only) |
| Roslyn | 4.8+ (shipped with .NET 8 SDK) |

**Runtime dependencies:**

| Package | Used by |
|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | Core, Generator (generated code) |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | Core, Generator (generated DI helpers) |

The `Generator` package has **zero runtime dependencies** — `Microsoft.CodeAnalysis.CSharp` is `PrivateAssets="all"` and never flows to the consuming project. The generated code depends only on `Microsoft.Extensions.Logging` and `Microsoft.Extensions.DependencyInjection`, which are already present in any .NET 8 application.

---

## License

MIT
