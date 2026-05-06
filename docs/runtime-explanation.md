# Runtime Explanation: How the Isolated Worker Model Discovers Library Functions

This document explains how Azure Functions discovers and runs the framework-shipped functions (`GenericOrchestrator`, `GenericSubOrchestration`, `StartWorkflow`, `EventTrigger`, `DeclarativeWorkflowPoller`) that live in the `DeclarativeDurableFunctions` library assembly — not in the consumer's entry-point project.

---

## 1. Build time — source generator runs

When you `dotnet build` the consumer app (e.g., `DeclarativeDurableFunctions.TestApp`), `Microsoft.Azure.Functions.Worker.Sdk` (version 2.x) includes a **Roslyn source generator** that runs as part of that compilation. It scans **both the entry-point assembly AND all referenced assemblies** in the compilation closure — including `DeclarativeDurableFunctions.dll`.

The output is a `functions.metadata` file. Notice the `scriptFile` field distinguishes which DLL each function lives in:

```json
// Activity from the consumer app:
{ "name": "ValidateOrderActivity", "scriptFile": "DeclarativeDurableFunctions.TestApp.dll", ... }

// Orchestrator from the library (auto-discovered):
{ "name": "GenericOrchestrator", "scriptFile": "DeclarativeDurableFunctions.dll", ... }
```

All five library functions appear in the consumer app's `functions.metadata` with the library DLL as the script file. The generator found them purely by scanning the reference assembly for `[Function]` attributes — no registration or stub required from the consumer.

> **This cross-assembly scanning is only true for `Microsoft.Azure.Functions.Worker.Sdk` 2.x.** The 1.x generator only scanned the entry-point project. This is why the spec targets SDK 2.x and why per-workflow orchestrator stubs were the norm in older documentation.

---

## 2. Deployment artifact

`dotnet publish` produces a directory containing:

- `DeclarativeDurableFunctions.TestApp.dll` — the entry-point executable
- `DeclarativeDurableFunctions.dll` — the library (copied because it is a project/package reference)
- `functions.metadata` — generated above; the Functions host reads this before the worker starts
- `worker.config.json` — tells the host how to launch the worker process
- `host.json`

---

## 3. Host startup on Azure

Two separate processes start:

### The Functions host (Azure's runtime — not your code)

- Reads `functions.metadata` from disk
- Registers all triggers: HTTP routes, orchestration triggers, durable client bindings, etc.
- Sees `"scriptFile": "DeclarativeDurableFunctions.dll"` and knows invocations for those functions must be routed to the library DLL
- Launches your worker process using `worker.config.json`

### Your worker process (`Program.cs`)

```csharp
new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()            // ①
    .ConfigureServices(services =>
        services.AddDeclarativeWorkflows())        // ②
    .Build()                                       // ③
    .RunAsync();                                   // ④
```

**①** `ConfigureFunctionsWorkerDefaults()` registers:
- `GeneratedFunctionMetadataProvider` — the source-generated class containing all function descriptors found at build time
- A gRPC channel back to the Functions host
- The invocation dispatch middleware pipeline

**②** `AddDeclarativeWorkflows()` loads all YAML files from `./Workflows/` at startup and registers `IWorkflowDefinitionRegistry` as a singleton. This is the only call consumers make — the library's functions are already in `functions.metadata` from step 1.

**③** `.Build()` resolves the DI container. `GenericOrchestrator`, `GenericSubOrchestrator`, and friends get their `IWorkflowDefinitionRegistry` constructor dependency wired here.

**④** `.RunAsync()`:
- Opens the gRPC channel to the Functions host
- Sends the function metadata from `GeneratedFunctionMetadataProvider` to the host as a runtime confirmation
- Enters the invocation loop — blocks, waiting for the host to send work

---

## 4. A trigger fires

When a request hits `POST /api/workflows/OrderFulfillment`:

1. The Azure Functions host routes it to `GenericHttpTrigger.StartAsync` (because `functions.metadata` says so)
2. The host sends an invocation request over gRPC to the worker
3. The worker dispatches to `GenericHttpTrigger.StartAsync`, which calls `DurableTaskClient.ScheduleNewOrchestrationInstanceAsync("GenericOrchestrator", ...)`
4. The host starts a new `GenericOrchestrator` instance
5. The host sends an orchestration invocation to the worker
6. The worker dispatches to `GenericOrchestrator.RunAsync(context)` with `IWorkflowDefinitionRegistry` injected from DI
7. `RunWorkflowDynamicAsync` reads the `__workflow` key from the input envelope, looks up the definition in the registry, and drives the YAML step tree

---

## Why this matters for the library design

The `DeclarativeDurableFunctions` library does **not** reference `Microsoft.Azure.Functions.Worker.Sdk`. Only the entry-point project (the consumer's Functions app) references it. When that app is compiled, the SDK's source generator crawls the full dependency closure, discovers the library's `[Function]`-decorated classes, and emits their metadata into the app's `functions.metadata` with `"scriptFile": "DeclarativeDurableFunctions.dll"`.

The result: adding `AddDeclarativeWorkflows()` to `Program.cs` is the only thing a consumer needs to do. The framework-level orchestrators, sub-orchestrators, HTTP trigger, event trigger, and poller are all registered automatically.
