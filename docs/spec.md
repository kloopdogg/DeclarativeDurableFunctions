# Implementation Spec: DeclarativeDurableFunctions

> This spec translates the design in [`vision.md`](vision.md) into a precise implementation plan. An agent should be able to execute this spec without additional clarification.

---

## 1. Issues and Decisions

These were identified while writing the spec. Each is resolved here; do not relitigate them.

### 1.1 API Version Mismatch (Breaking)

`vision.md` code examples use the **in-process** Durable Functions API (`IDurableOrchestrationContext`, `[FunctionName]`). The target is the **isolated worker** model, which uses a completely different API. All code in this project must use:

| In-process (do NOT use) | Isolated worker (USE THIS) |
|---|---|
| `Microsoft.Azure.WebJobs.Extensions.DurableTask` | `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` |
| `[FunctionName("X")]` | `[Function("X")]` |
| `IDurableOrchestrationContext` | `TaskOrchestrationContext` (from `Microsoft.DurableTask`) |
| `IDurableActivityContext` | `TaskActivityContext` (from `Microsoft.DurableTask`) |
| `IDurableOrchestrationClient` | `DurableTaskClient` (from `Microsoft.DurableTask`) |
| `[DurableClient] IDurableOrchestrationClient` | `[DurableClient] DurableTaskClient` |
| Static function methods | Instance methods in DI-constructed classes |
| `CallSubOrchestratorAsync(..., instanceId)` (positional) | `CallSubOrchestratorAsync(..., new SubOrchestrationOptions { InstanceId = id })` |
| `StartOrchestrationAsync(name, input, instanceId)` (positional) | `ScheduleNewOrchestrationInstanceAsync(name, input, new StartOrchestrationOptions(instanceId))` |

`SubOrchestrationOptions` lives in `Microsoft.DurableTask` and inherits from `TaskOptions`, which also carries retry policy. This means sub-orchestration instance ID and retry are both set through `SubOrchestrationOptions` — there is no positional instance ID overload in the isolated worker model.

`StartOrchestrationOptions` also lives in `Microsoft.DurableTask` but is the **client-side** type used when scheduling a new top-level orchestration from a trigger function. It has `InstanceId` (string) and `StartAt` (DateTimeOffset?) properties. It does **not** inherit `TaskOptions` and has no retry policy — retry is not applicable when scheduling, only when calling from within an orchestrator.

### 1.2 Dependency Injection for Registry

The vision shows a parameter-less `context.RunWorkflow()`. In the isolated worker model, static methods cannot receive constructor-injected services. The orchestrator stub must be an **instance class** receiving `IWorkflowDefinitionRegistry` via constructor injection:

```csharp
public class OrderFulfillmentOrchestrator(IWorkflowDefinitionRegistry registry)
{
    [Function("OrderFulfillment")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowAsync(registry);
}
```

The extension method signature is `RunWorkflowAsync(this TaskOrchestrationContext context, IWorkflowDefinitionRegistry registry)`.

### 1.3 Step Output Types

All step outputs arrive from Durable Functions as JSON-deserialized values. Use `System.Text.Json.JsonElement` as the canonical output type. This is transparent to activity developers — they return normal C# types; the runtime serializes them to JSON and the framework calls `CallActivityAsync<JsonElement>` so it never needs to know the concrete type at compile time.

Three implementation rules follow from this:

1. **Property traversal** — `{{approval.daysWaited > 3}}` means: get `approval` from context (a `JsonElement`), call `.GetProperty("daysWaited")`, read its numeric value, compare to `3`. Use the standard `System.Text.Json` API throughout the `ExpressionEvaluator`.

2. **Missing properties** — `JsonElement` throws on `.GetProperty()` when a key is absent; it does not return `null`. The evaluator must use `TryGetProperty` and treat a miss as `null`/falsy. Failure to do this turns a YAML typo into an orchestration crash.

3. **Cloning before storage** — `JsonElement` is a struct that points into an owning `JsonDocument`. If that document is collected, the element becomes invalid. `WorkflowExecutionContext.SetOutput()` must call `element.Clone()` on any `JsonElement` before storing it, producing a self-contained copy that owns its own memory.

### 1.4 WaitForExternalEvent Typing

`TaskOrchestrationContext.WaitForExternalEvent<T>()` requires a compile-time type. Since workflows are schema-agnostic, use `T = JsonElement`. The YAML `output` variable receives the `JsonElement` directly.

### 1.5 Sub-Orchestration Instance IDs

Sub-orchestration instance IDs must be deterministic across replays (Durable Functions requirement). The framework resolves them in this priority order:

1. **YAML-prescribed** — if `instanceId` is set on the step, evaluate it as an expression. All `{{...}}` forms are valid, including `{{$item.orderId}}`, `{{$index}}`, `{{input.someId}}`, and composites like `"{{$item.region}}-{{$item.id}}"`.
2. **Default** — `context.NewGuid()`, which generates a GUID that is deterministic across replays (seeded by the orchestration event history).

**Never use `Guid.NewGuid()` inside the runner.** It is non-deterministic — a different value on every replay causes the runtime to try to start new sub-orchestrations instead of finding the already-completed ones in history.

Full instance ID format: `{parentInstanceId}:{stepName}:{resolvedSuffix}`

### 1.6 ExecutionContext Name Collision

`System.Threading.ExecutionContext` exists in the BCL. Name our class `WorkflowExecutionContext` throughout.

### 1.7 `on-timeout` Scope

`on-timeout` supports two values:
- `fail` (default) — throw `WorkflowTimeoutException`; orchestration fails
- `continue` — set step output to `null`; proceed to next step

Escalation patterns are composed using a `condition` on a subsequent step:

```yaml
- name: WaitForApproval
  type: wait-for-event
  event: OrderApproved
  timeout: P7D
  on-timeout: continue
  output: approval

- name: EscalateApproval
  activity: EscalateApprovalActivity
  input: "{{input.orderId}}"
  condition: "{{approval == null}}"
```

### 1.8 YAML File Deployment

YAML workflow files are part of the Functions app, not the library. In the TestApp `.csproj`:
```xml
<ItemGroup>
  <Content Include="Workflows\**\*.yaml">
    <CopyToOutputDirectory>Always</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### 1.9 Condition Expression Scope

`condition` expressions must return a boolean. The evaluator supports a defined subset of operators (see §6.2). Expressions that do not return a truthy value cause the step to be skipped entirely (no error thrown).

### 1.10 Workflow Name Resolution

The workflow name is the YAML **filename without extension**, case-preserved. The `workflow.name` field inside the YAML is optional metadata (used for logging only). The `[Function("X")]` attribute value on the orchestrator stub MUST exactly match the YAML filename (case-sensitive; Azure runs on Linux).

---

## 2. Solution Structure

### 2.1 File Layout

```
DeclarativeDurableFunctions.slnx
src/
  DeclarativeDurableFunctions/
    DeclarativeDurableFunctions.csproj
    Models/
      WorkflowDefinition.cs
      StepDefinition.cs
      StepType.cs
      RetryPolicy.cs
      SwitchCase.cs
      WorkflowInput.cs          # public API types shipped to library consumers
    Engine/
      WorkflowDefinitionLoader.cs
      WorkflowDefinitionRegistry.cs
      WorkflowExecutionContext.cs
      ExpressionEvaluator.cs
      WorkflowRunner.cs
    Extensions/
      ServiceCollectionExtensions.cs
      OrchestrationContextExtensions.cs
    Exceptions/
      WorkflowDefinitionException.cs
      WorkflowTimeoutException.cs
      WorkflowExpressionException.cs
  DeclarativeDurableFunctions.TestApp/
    DeclarativeDurableFunctions.TestApp.csproj
    host.json
    local.settings.json
    Program.cs
    Workflows/
      OrderFulfillment.yaml
      FulfillLineItem.yaml
    Activities/
      ValidateOrderActivity.cs
      ReserveItemActivity.cs
      FulfillLineItemActivity.cs
      SendConfirmationEmailActivity.cs
      UpdateInventoryActivity.cs
    Orchestrators/
      OrderFulfillmentOrchestrator.cs
      FulfillLineItemOrchestrator.cs
    Models/
      Order.cs
      LineItem.cs
      ValidationResult.cs
      ReservationResult.cs
      FulfillmentResult.cs
tests/
  DeclarativeDurableFunctions.Tests/
    DeclarativeDurableFunctions.Tests.csproj
    Unit/
      ExpressionEvaluatorTests.cs
      WorkflowDefinitionLoaderTests.cs
      WorkflowRunnerTests.cs
    Fixtures/
      WorkflowYaml.cs           # inline YAML strings for tests
```

### 2.2 .slnx Format

```xml
<Solution>
  <Project Path="src/DeclarativeDurableFunctions/DeclarativeDurableFunctions.csproj" />
  <Project Path="src/DeclarativeDurableFunctions.TestApp/DeclarativeDurableFunctions.TestApp.csproj" />
  <Project Path="tests/DeclarativeDurableFunctions.Tests/DeclarativeDurableFunctions.Tests.csproj" />
</Solution>
```

---

## 3. Project: `DeclarativeDurableFunctions` (Class Library)

### 3.1 Target Framework and Package References

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DeclarativeDurableFunctions</PackageId>
  </PropertyGroup>

  <ItemGroup>
    <!-- Durable Functions isolated worker SDK -->
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.DurableTask" Version="1.*" />
    <!-- YAML parsing -->
    <PackageReference Include="YamlDotNet" Version="16.*" />
  </ItemGroup>
</Project>
```

No other dependencies. `System.Text.Json` is in the BCL. Do not add Newtonsoft.Json.

### 3.2 Public API Surface

The following types are public and form the library's contract:

- `IWorkflowDefinitionRegistry` — lookup interface
- `WorkflowDefinitionRegistryOptions` — configuration (workflows directory path)
- `WorkflowInput<TData>` — envelope base type for consumers
- `WorkflowMetadata` — parent metadata carrier
- `OrchestrationContextExtensions.RunWorkflowAsync()` — the orchestrator extension method
- `ServiceCollectionExtensions.AddDeclarativeWorkflows()` — DI registration
- `WorkflowDefinitionException` — thrown on invalid YAML
- `WorkflowTimeoutException` — thrown on `on-timeout: fail`
- `WorkflowExpressionException` — thrown on expression evaluation failure

All engine internals (`WorkflowRunner`, `ExpressionEvaluator`, `WorkflowExecutionContext`, etc.) are `internal`.

---

## 4. Project: `DeclarativeDurableFunctions.TestApp` (Azure Functions App)

### 4.1 Target Framework and Package References

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.DurableTask" Version="1.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.*" />
    <ProjectReference Include="..\..\src\DeclarativeDurableFunctions\DeclarativeDurableFunctions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="Workflows\**\*.yaml">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
</Project>
```

### 4.2 `Program.cs`

```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddDeclarativeWorkflows(); // scans ./Workflows/ by default
    })
    .Build();

await host.RunAsync();
```

### 4.3 Sample Workflows

The TestApp ships two workflows that exercise all supported step types:

**`OrderFulfillment.yaml`** — exercises: sequential activity, foreach sub-orchestration, wait-for-event, parallel block, conditional step.

**`FulfillLineItem.yaml`** — exercises: sequential activities, retry policy.

Full YAML content is specified in §5.

### 4.4 Orchestrator Stubs

Each YAML workflow needs an orchestrator stub class. The `[Function]` name MUST match the YAML filename exactly.

```csharp
// Orchestrators/OrderFulfillmentOrchestrator.cs
public class OrderFulfillmentOrchestrator(IWorkflowDefinitionRegistry registry)
{
    [Function("OrderFulfillment")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowAsync(registry);
}
```

### 4.5 Activity Function Pattern

Activities use isolated worker conventions. The `TaskActivityContext` parameter is optional (include it only if needed for logging/instance ID):

```csharp
public class ValidateOrderActivity
{
    [Function("ValidateOrderActivity")]
    public ValidationResult Run([ActivityTrigger] Order input, FunctionContext executionContext)
    {
        // business logic here
        return new ValidationResult { IsValid = true };
    }
}
```

For activities receiving the envelope type:
```csharp
public class FulfillLineItemActivity
{
    [Function("FulfillLineItemActivity")]
    public async Task<FulfillmentResult> RunAsync(
        [ActivityTrigger] WorkflowInput<LineItem> input, FunctionContext executionContext)
    {
        // input.Parent.OrchestrationId available for correlation
        // input.Data is the LineItem
    }
}
```

### 4.6 Trigger Function Pattern

Trigger functions (HTTP, Service Bus, Timer, etc.) start orchestrations using `DurableTaskClient`. To set a custom instance ID, pass `StartOrchestrationOptions` — there is no positional instance ID overload in the isolated worker model.

```csharp
public class OrderFulfillmentHttpTrigger
{
    [Function("StartOrderFulfillment")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var order = await req.ReadFromJsonAsync<Order>();

        // Use a natural business key as the instance ID so duplicate requests are idempotent.
        var options = new StartOrchestrationOptions(instanceId: order!.OrderId);
        await client.ScheduleNewOrchestrationInstanceAsync("OrderFulfillment", order, options);

        return client.CreateCheckStatusResponse(req, order.OrderId);
    }
}
```

- `StartOrchestrationOptions` is in `Microsoft.DurableTask`. Its two properties are `InstanceId` (string) and `StartAt` (DateTimeOffset?). Pass `instanceId` via constructor or object initializer; omit to let the runtime generate a GUID.
- Use a natural business key (e.g. `orderId`) when one exists — this makes re-triggering idempotent.
- `[DurableClient] DurableTaskClient` is the isolated worker binding. Do **not** use `IDurableOrchestrationClient`.

---

## 5. YAML Schema Reference

### 5.1 Top-Level Structure

```yaml
workflow:
  name: <string>      # optional; metadata only; workflow identity comes from filename
  steps:
    - <step>
    - <step>
```

### 5.2 Step Fields (All Types)

| Field | Type | Required | Notes |
|---|---|---|---|
| `name` | string | No* | Required if the step's output is referenced by a later step. Must be unique within the workflow. |
| `type` | string | No | Inferred when `activity` or `workflow` field is present (see §5.3). Explicit for `foreach`, `parallel`, `wait-for-event`, `switch`. |
| `input` | expression or object | No | The input to pass to the activity/sub-orchestration. Omit for activities that take no input. |
| `output` | string | No | Variable name stored in `WorkflowExecutionContext` after the step completes. Available in all subsequent `{{...}}` expressions. |
| `condition` | expression | No | Boolean expression. If present and evaluates to `false`/falsy, the step is skipped. No error if skipped. |

### 5.3 Type Inference Rules

1. If `activity` field is present → `type: activity` (even if `type` is omitted).
2. If `workflow` field is present and `type` is omitted → `type: sub-orchestration`.
3. If `workflow` field is present and `type: foreach` → foreach over sub-orchestration.
4. If `activity` field is present and `type: foreach` → foreach over activity.
5. `type` must be one of: `activity`, `sub-orchestration`, `foreach`, `parallel`, `wait-for-event`, `switch`. Any other value is a `WorkflowDefinitionException`.

### 5.4 Activity Step

```yaml
- name: ValidateOrder          # optional
  activity: ValidateOrderActivity
  input: "{{input}}"           # optional; whole-value expression or inline object
  output: validation           # optional
  condition: "{{input.orderTotal > 0}}"  # optional
  retry:                       # optional
    maxAttempts: 3             # required if retry block is present; int >= 1
    firstRetryInterval: PT5S   # optional; ISO 8601 duration; default PT1S
    maxRetryInterval: PT1M     # optional; ISO 8601 duration
    backoffCoefficient: 2.0    # optional; float >= 1.0; default 1.0
```

`retry` maps to `TaskRetryPolicy` in the isolated worker SDK.

### 5.5 Sub-Orchestration Step

```yaml
- name: RunSubWorkflow
  type: sub-orchestration
  workflow: OrderValidation     # required; must match a registered workflow filename
  input: "{{input}}"           # optional
  output: validationResult     # optional
  instanceId: "{{input.orderId}}"  # optional; expression for sub-orchestration instance ID suffix
  condition: "{{...}}"         # optional
  retry:                       # optional; same schema as activity retry
    maxAttempts: 3
    firstRetryInterval: PT5S
```

- `instanceId` is optional. If omitted, `context.NewGuid()` is used as the suffix.
- Full instance ID: `{parentInstanceId}:{stepName}:{resolvedSuffix}`
- `step.Name` is required when `type: sub-orchestration` is explicit.
- `retry` maps to `TaskRetryPolicy` and is passed through `SubOrchestrationOptions` (which inherits `TaskOptions`). Both instance ID and retry policy are set on the same options object — there is no separate `TaskOptions` for sub-orchestration calls.

### 5.6 Foreach Step

```yaml
- name: ReserveInventory
  type: foreach
  source: "{{input.lineItems}}"  # required; must evaluate to a JSON array
  activity: ReserveItemActivity  # mutually exclusive with workflow
  # workflow: SomeWorkflow       # alternative: foreach over sub-orchestration
  input:                         # optional; $item and $index available in expressions
    parent:
      orchestrationId: "{{orchestration.instanceId}}"
      correlationId: "{{input.correlationId}}"
    data: "{{$item}}"
  instanceId: "{{$item.id}}"    # optional; expression for sub-orchestration ID suffix
                                 # only meaningful when workflow is set (activities have no instance ID)
                                 # omit to use context.NewGuid()
  output: reservations           # optional; receives array of results in source order
  condition: "{{...}}"           # optional; evaluated once before the fan-out
  retry:                         # optional; applied to each item's activity or sub-orchestration call
    maxAttempts: 3
    firstRetryInterval: PT5S
```

- `activity` and `workflow` are mutually exclusive. Exactly one must be present.
- Fan-out uses `Task.WhenAll`. Results are collected in the same order as `source`.
- `instanceId` is only applied when `workflow` is set. When `activity` is set, `instanceId` is ignored (activities have no instance ID).
- If `instanceId` is omitted and `workflow` is set, `context.NewGuid()` is used per iteration.
- `$item` and `$index` are available in both `input` and `instanceId` expressions.
- `$item` is the current element; `$index` is the 0-based integer index. Both are only valid within this step.

### 5.7 Parallel Step

```yaml
- name: Finalize
  type: parallel
  steps:
    - name: SendConfirmation
      activity: SendConfirmationEmailActivity
      input: "{{input.customerEmail}}"
    - name: UpdateLedger
      type: sub-orchestration
      workflow: LedgerUpdate
      input: "{{fulfillmentResults}}"
  output: finalizeResults   # optional; receives object keyed by child step name
  condition: "{{...}}"      # optional; evaluated before launching parallel steps
```

- Each step in `steps` runs concurrently via `Task.WhenAll`.
- Named child steps write their outputs into the parent `WorkflowExecutionContext` after all complete.
- `output` on the parallel block (if present) receives a `Dictionary<string, object?>` of all named child outputs.
- Anonymous child steps (no `name`) may not be referenced later; their output is discarded.

### 5.8 Wait-for-Event Step

```yaml
- name: WaitForApproval
  type: wait-for-event
  event: OrderApproved      # required; event name passed to RaiseEventAsync
  timeout: P7D              # optional; ISO 8601 duration; omit to wait indefinitely
  on-timeout: fail          # optional; fail (default) | continue
  output: approval          # optional; receives JsonElement of event payload
  condition: "{{...}}"      # optional; if false, step is skipped entirely
```

- When `timeout` is present: races `WaitForExternalEvent<JsonElement>` against `CreateTimer`.
- When `on-timeout: fail`: throws `WorkflowTimeoutException(stepName, timeout)`.
- When `on-timeout: continue`: sets the output variable to `null` and proceeds.
- When `timeout` is absent: `WaitForExternalEvent<JsonElement>` with no timer race (waits forever).

### 5.9 Switch Step

```yaml
- name: RouteByRegion
  type: switch
  on: "{{input.region}}"    # required; expression that evaluates to a string or number
  cases:
    EU:
      - activity: ApplyEUComplianceActivity
      - activity: RouteToEUFulfillmentActivity
    US:
      - activity: RouteToUSFulfillmentActivity
    default:                # optional; matched when no case key matches
      - activity: RouteToGlobalFulfillmentActivity
  condition: "{{...}}"      # optional
```

- Case keys are strings. Comparison against the evaluated `on` expression is **string comparison, case-sensitive** (convert number results to string before comparing).
- The `default` key is reserved and matched last.
- If no case matches and there is no `default`, the switch step is a no-op (no error).
- Steps within a case inherit the parent `WorkflowExecutionContext` and may write named outputs into it.

### 5.10 Complete Example: `OrderFulfillment.yaml`

```yaml
workflow:
  name: OrderFulfillment
  steps:
    - name: ValidateOrder
      activity: ValidateOrderActivity
      input: "{{input}}"
      output: validation
      retry:
        maxAttempts: 3
        firstRetryInterval: PT5S

    - name: FulfillLineItems
      type: foreach
      source: "{{input.lineItems}}"
      workflow: FulfillLineItem
      input:
        parent:
          orchestrationId: "{{orchestration.instanceId}}"
          correlationId: "{{input.correlationId}}"
        data: "{{$item}}"
      output: fulfillmentResults

    - name: WaitForApproval
      type: wait-for-event
      event: OrderApproved
      timeout: P7D
      on-timeout: fail
      output: approval

    - name: ChargeLateFee
      activity: ChargeLateFeeActivity
      input: "{{input.orderId}}"
      condition: "{{approval.daysWaited > 3}}"

    - name: Finalize
      type: parallel
      steps:
        - name: SendConfirmation
          activity: SendConfirmationEmailActivity
          input: "{{input.customerEmail}}"
        - name: UpdateInventory
          activity: UpdateInventoryActivity
          input: "{{fulfillmentResults}}"
```

### 5.11 Complete Example: `FulfillLineItem.yaml`

```yaml
workflow:
  name: FulfillLineItem
  steps:
    - name: ReserveItem
      activity: ReserveItemActivity
      input: "{{input.data}}"
      output: reservation
      retry:
        maxAttempts: 5
        firstRetryInterval: PT2S
        backoffCoefficient: 2.0
        maxRetryInterval: PT30S

    - name: FulfillItem
      activity: FulfillLineItemActivity
      input:
        parent: "{{input.parent}}"
        data: "{{reservation}}"
      output: fulfillment
```

---

## 6. Expression Language

### 6.1 Syntax

Expressions are delimited by `{{` and `}}`. They appear as YAML string values.

**Whole-value rule**: If the entire YAML value is exactly `"{{expr}}"` (no other text), the result preserves the resolved type (object, array, number, bool, null). If the expression is embedded within a string (`"Order {{$item.id}} shipped"`), all expressions in the string are coerced to string and interpolated.

### 6.2 Supported Expression Forms

| Form | Description |
|---|---|
| `{{input}}` | Full workflow input (`JsonElement`) |
| `{{input.field}}` | Property access on workflow input |
| `{{input.a.b.c}}` | Nested property access (any depth) |
| `{{stepName}}` | Output of a named step (`JsonElement` or object) |
| `{{stepName.field}}` | Property access on step output |
| `{{$item}}` | Current foreach item (foreach scope only) |
| `{{$item.field}}` | Property on current foreach item |
| `{{$index}}` | 0-based foreach index (integer) |
| `{{orchestration.instanceId}}` | Current orchestration instance ID (string) |
| `{{orchestration.parentInstanceId}}` | Parent instance ID (string or null) |

**Condition-only forms** (only valid in `condition` fields and `switch.on`):

| Form | Description |
|---|---|
| `{{a > b}}` | Greater than (numeric comparison) |
| `{{a < b}}` | Less than |
| `{{a >= b}}` | Greater than or equal |
| `{{a <= b}}` | Less than or equal |
| `{{a == b}}` | Equality (string or number) |
| `{{a != b}}` | Inequality |
| `{{a && b}}` | Logical AND |
| `{{a \|\| b}}` | Logical OR |
| `{{!a}}` | Logical NOT |

Where `a` and `b` are either a property-access path (as above) or a literal (string in quotes, integer, float, `true`, `false`, `null`).

Parentheses are supported for grouping: `{{(a > 0) && (b != null)}}`.

### 6.3 Property Access on JsonElement

When traversing a `JsonElement`, use `JsonElement.GetProperty(name)`. If a property does not exist:
- In a condition expression: evaluate as `null` (falsy)
- In an `input` expression: throw `WorkflowExpressionException` with message including the missing path

### 6.4 Implementation Note

The expression evaluator is a custom recursive descent parser. It does NOT use `eval`, `dynamic`, or Roslyn. A regex-only approach is insufficient for nested conditions. The parser handles tokenization (path segments, literals, operators) and evaluation in a single pass over the expression string. Suggested approach: `ExpressionParser` class that builds a simple AST (`PathNode`, `LiteralNode`, `BinaryOpNode`, `UnaryOpNode`), then `ExpressionEvaluator` that walks the AST against a `WorkflowExecutionContext`.

---

## 7. C# Type Definitions

### 7.1 Public Types

```csharp
// Models/WorkflowInput.cs
public class WorkflowInput<TData>
{
    public WorkflowMetadata Parent { get; set; } = default!;
    public TData Data { get; set; } = default!;
}

public class WorkflowMetadata
{
    public string OrchestrationId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? WorkflowName { get; set; }
}

// Extensions/ServiceCollectionExtensions.cs
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeclarativeWorkflows(
        this IServiceCollection services,
        Action<WorkflowDefinitionRegistryOptions>? configure = null);
}

public class WorkflowDefinitionRegistryOptions
{
    public string WorkflowsDirectory { get; set; } = "Workflows";
}

// Public interface — WorkflowDefinition is internal, so Get()/TryGet() live on an
// internal extension interface (IWorkflowDefinitionRegistryInternal) used only by the engine.
// Consumers inject IWorkflowDefinitionRegistry and pass it to RunWorkflowAsync(); they
// never call Get() directly.
public interface IWorkflowDefinitionRegistry
{
    IReadOnlyCollection<string> WorkflowNames { get; }
}

// Engine-internal only — not visible to library consumers
internal interface IWorkflowDefinitionRegistryInternal : IWorkflowDefinitionRegistry
{
    WorkflowDefinition Get(string workflowName);
    bool TryGet(string workflowName, out WorkflowDefinition? definition);
}

// Extensions/OrchestrationContextExtensions.cs
public static class OrchestrationContextExtensions
{
    public static Task RunWorkflowAsync(
        this TaskOrchestrationContext context,
        IWorkflowDefinitionRegistry registry);
}

// Exceptions
public class WorkflowDefinitionException(string message, string? workflowName = null, Exception? inner = null)
    : Exception(message, inner);

public class WorkflowTimeoutException(string stepName, string timeout)
    : Exception($"Step '{stepName}' timed out after {timeout}");

public class WorkflowExpressionException(string expression, string reason, Exception? inner = null)
    : Exception($"Expression '{expression}' failed: {reason}", inner);
```

### 7.2 Internal Model Types

```csharp
// Models/StepType.cs
internal enum StepType
{
    Activity,
    SubOrchestration,
    Foreach,
    Parallel,
    WaitForEvent,
    Switch
}

// Models/RetryPolicy.cs
internal sealed class RetryPolicy
{
    public int MaxAttempts { get; init; }
    public string FirstRetryInterval { get; init; } = "PT1S";  // ISO 8601
    public string? MaxRetryInterval { get; init; }
    public double BackoffCoefficient { get; init; } = 1.0;

    public TaskRetryPolicy ToTaskRetryPolicy();  // converts to SDK type
}

// Models/StepDefinition.cs
internal sealed class StepDefinition
{
    public string? Name { get; init; }
    public StepType Type { get; init; }
    public string? ActivityName { get; init; }
    public string? WorkflowName { get; init; }
    public object? Input { get; init; }           // string (expression) or Dictionary (object template)
    public string? Output { get; init; }
    public string? Condition { get; init; }
    public RetryPolicy? Retry { get; init; }

    // Foreach / SubOrchestration
    public string? Source { get; init; }      // foreach only
    public string? InstanceId { get; init; }  // optional expression; sub-orchestration and foreach+workflow only

    // Parallel
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];

    // WaitForEvent
    public string? EventName { get; init; }
    public string? Timeout { get; init; }
    public string OnTimeout { get; init; } = "fail";  // "fail" | "continue"

    // Switch
    public string? SwitchOn { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> Cases { get; init; }
        = new Dictionary<string, IReadOnlyList<StepDefinition>>();
}

// Models/WorkflowDefinition.cs
internal sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;   // from filename
    public string? DisplayName { get; init; }            // from workflow.name in YAML
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
}
```

### 7.3 Internal Engine Types

```csharp
// Engine/WorkflowExecutionContext.cs
internal sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(JsonElement input, TaskOrchestrationContext orchestrationContext);

    public JsonElement Input { get; }
    public string InstanceId { get; }
    public string? ParentInstanceId { get; }

    public void SetOutput(string name, object? value);
    public object? GetOutput(string name);
    public bool HasOutput(string name);

    // Creates a child scope for foreach iterations
    public WorkflowExecutionContext CreateIterationScope(JsonElement item, int index);
}

// Engine/WorkflowDefinitionRegistry.cs (implements IWorkflowDefinitionRegistry)
internal sealed class WorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
{
    // Populated at construction time by WorkflowDefinitionLoader
    // Read-only after construction; thread-safe
}

// Engine/WorkflowDefinitionLoader.cs
internal static class WorkflowDefinitionLoader
{
    public static IReadOnlyDictionary<string, WorkflowDefinition> LoadAll(string directory);
    public static WorkflowDefinition LoadFromYaml(string yaml, string workflowName);
}

// Engine/ExpressionEvaluator.cs
internal static class ExpressionEvaluator
{
    public static object? Evaluate(string expression, WorkflowExecutionContext ctx);
    public static bool EvaluateBool(string expression, WorkflowExecutionContext ctx);
    public static object? ResolveInputTemplate(object? inputTemplate, WorkflowExecutionContext ctx);
}

// Engine/WorkflowRunner.cs
internal sealed class WorkflowRunner
{
    public Task RunAsync(
        TaskOrchestrationContext context,
        WorkflowDefinition definition,
        WorkflowExecutionContext execCtx);
}
```

---

## 8. Engine Implementation Guide

### 8.1 DI Registration (`AddDeclarativeWorkflows`)

1. Read `WorkflowDefinitionRegistryOptions.WorkflowsDirectory`.
2. Resolve to absolute path relative to the process's base directory (`AppContext.BaseDirectory`).
3. Call `WorkflowDefinitionLoader.LoadAll(directory)` — eager, at startup.
4. Register a `WorkflowDefinitionRegistry` singleton wrapping the loaded definitions.
5. Register `IWorkflowDefinitionRegistry` → `WorkflowDefinitionRegistry`.

Throw `WorkflowDefinitionException` at startup (during `AddDeclarativeWorkflows`) if any YAML file fails to parse. Do not fail silently.

### 8.2 `RunWorkflowAsync` Extension Method

```
1. workflow = registry.Get(context.Name)  // throws if not found
2. inputJson = context.GetInput<JsonElement>() ?? JsonElement (empty object)
3. execCtx = new WorkflowExecutionContext(inputJson, context)
4. runner = new WorkflowRunner()
5. await runner.RunAsync(context, workflow, execCtx)
```

### 8.3 `WorkflowRunner.RunAsync` — Step Dispatch

```
For each step in definition.Steps:
  1. If step.Condition is set:
       result = ExpressionEvaluator.EvaluateBool(step.Condition, execCtx)
       if result == false: skip step (continue loop)
  2. Dispatch by step.Type:
       Activity          → DispatchActivity(context, step, execCtx)
       SubOrchestration  → DispatchSubOrchestration(context, step, execCtx)
       Foreach           → DispatchForeach(context, step, execCtx)
       Parallel          → DispatchParallel(context, step, execCtx)
       WaitForEvent      → DispatchWaitForEvent(context, step, execCtx)
       Switch            → DispatchSwitch(context, step, execCtx)
  3. If step.Output is set: execCtx.SetOutput(step.Output, result)
```

### 8.4 Activity Dispatch

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)
options = step.Retry != null ? new TaskOptions(step.Retry.ToTaskRetryPolicy()) : null
result = await context.CallActivityAsync<JsonElement>(step.ActivityName, resolvedInput, options)
return result
```

### 8.5 Sub-Orchestration Dispatch

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)
instanceIdSuffix = step.InstanceId != null
    ? ExpressionEvaluator.Evaluate(step.InstanceId, execCtx)?.ToString()
    : context.NewGuid().ToString()
instanceId = $"{execCtx.InstanceId}:{step.Name}:{instanceIdSuffix}"
retryPolicy = step.Retry?.ToTaskRetryPolicy()
options = new SubOrchestrationOptions(retryPolicy) { InstanceId = instanceId }
result = await context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName, resolvedInput, options)
return result
```

`SubOrchestrationOptions` is from `Microsoft.DurableTask` and inherits `TaskOptions`, which carries the retry policy. Both instance ID and retry are set on the same options object — there is no positional `instanceId` overload and no separate `TaskOptions` for sub-orchestration calls.

### 8.6 Foreach Dispatch

```
source = ExpressionEvaluator.Evaluate(step.Source, execCtx)  // must be JsonElement array
items = source.EnumerateArray().ToList()

tasks = items.Select((item, index) =>
{
    iterCtx = execCtx.CreateIterationScope(item, index)
    resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, iterCtx)

    if step.ActivityName != null:
        activityOptions = step.Retry != null ? new TaskOptions(step.Retry.ToTaskRetryPolicy()) : null
        return context.CallActivityAsync<JsonElement>(step.ActivityName, resolvedInput, activityOptions)
    else:
        instanceIdSuffix = step.InstanceId != null
            ? ExpressionEvaluator.Evaluate(step.InstanceId, iterCtx)?.ToString()
            : context.NewGuid().ToString()
        instanceId = $"{execCtx.InstanceId}:{step.Name}:{instanceIdSuffix}"
        retryPolicy = step.Retry?.ToTaskRetryPolicy()
        options = new SubOrchestrationOptions(retryPolicy) { InstanceId = instanceId }
        return context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName, resolvedInput, options)
})

results = await Task.WhenAll(tasks)
return results  // array in source order; stored as object[] in execCtx
```

If `source` does not evaluate to a JSON array: throw `WorkflowExpressionException`.

### 8.7 Parallel Dispatch

```
tasks = step.Steps.Select(childStep =>
    DispatchStep(context, childStep, execCtx)  // reuses main dispatch logic
)
results = await Task.WhenAll(tasks)

// Write named child outputs into execCtx
for (childStep, result) in zip(step.Steps, results):
    if childStep.Output != null:
        execCtx.SetOutput(childStep.Output, result)

if step.Output != null:
    outputDict = step.Steps
        .Where(s => s.Name != null)
        .Zip(results)
        .ToDictionary(x => x.First.Name!, x => x.Second)
    return outputDict

return null
```

### 8.8 WaitForEvent Dispatch

```
eventTask = context.WaitForExternalEvent<JsonElement>(step.EventName)

if step.Timeout == null:
    return await eventTask

timerTask = context.CreateTimer(
    context.CurrentUtcDateTime + ParseIso8601Duration(step.Timeout),
    CancellationToken.None)

completed = await Task.WhenAny(eventTask, timerTask)

if completed == timerTask:
    // timed out
    if step.OnTimeout == "fail":
        throw new WorkflowTimeoutException(step.Name ?? step.EventName, step.Timeout)
    else:  // "continue"
        return null
else:
    timerTask.Dispose()  // cancel the timer (call context.CreateTimer with CancellationToken)
    return await eventTask
```

**Note**: Use a `CancellationTokenSource` to cancel the losing branch in `WhenAny`. In isolated worker, `context.CreateTimer` accepts a `CancellationToken`. Pass a CTS token to the timer; cancel it if the event fires first.

### 8.9 Switch Dispatch

```
switchValue = ExpressionEvaluator.Evaluate(step.SwitchOn, execCtx)?.ToString() ?? ""

matchingCase = step.Cases.ContainsKey(switchValue)
    ? step.Cases[switchValue]
    : step.Cases.ContainsKey("default")
        ? step.Cases["default"]
        : null

if matchingCase == null: return null  // no match, no default — silent no-op

foreach childStep in matchingCase:
    await DispatchStep(context, childStep, execCtx)

return null
```

### 8.10 YAML Parsing (`WorkflowDefinitionLoader`)

Use **YamlDotNet** to deserialize YAML into an intermediate dictionary model, then map to `WorkflowDefinition`. Do not use YamlDotNet's direct deserialization to the model types (mapping manually gives better validation error messages).

Validation rules (throw `WorkflowDefinitionException` for any violation):
1. `workflow.steps` must be a sequence.
2. Each step must resolve to a known `StepType` (see §5.3 inference rules).
3. `foreach` step must have exactly one of `activity` or `workflow`.
4. `foreach` step must have `source`.
5. `parallel` step must have `steps` as a sequence.
6. `wait-for-event` step must have `event`.
7. `switch` step must have `on` and `cases`.
8. `retry.maxAttempts` must be >= 1 if present.
9. `on-timeout` must be `fail` or `continue` if present.
10. Step `name` must be unique within a workflow (across all nesting levels — warn but do not throw if duplicate names exist in different parallel/switch branches).

ISO 8601 duration parsing: use a helper that converts `PT5S` → `TimeSpan.FromSeconds(5)`, `P7D` → `TimeSpan.FromDays(7)`, etc. Standard .NET does not parse ISO 8601 durations natively; implement a minimal parser (only `P`, `D`, `H`, `M`, `S` components needed).

### 8.11 Determinism Constraints

The `WorkflowRunner` runs inside the orchestrator and is subject to Durable Functions replay rules:
- No `DateTime.Now` or `DateTime.UtcNow` — use `context.CurrentUtcDateTime`.
- No random number generation.
- No file I/O, network calls, or logging inside the runner.
- No `Thread.Sleep` or `Task.Delay` — use `context.CreateTimer`.
- The `WorkflowDefinitionRegistry` is injected (loaded at startup, not inside the runner) — this is correct.

The `ExpressionEvaluator` is pure in-memory computation — safe inside the orchestrator.

---

## 9. Project: `DeclarativeDurableFunctions.Tests` (xUnit)

**Framework choice: xUnit**. Rationale: de facto standard in .NET, first-class `async Task` test support, parallel test execution by default, used in all Microsoft Durable Functions samples. No MSTest or NUnit.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="FluentAssertions" Version="7.*" />
    <ProjectReference Include="..\..\src\DeclarativeDurableFunctions\DeclarativeDurableFunctions.csproj" />
  </ItemGroup>
</Project>
```

**Mocking**: Use **NSubstitute** to mock `TaskOrchestrationContext` (which is abstract). Do not use Moq — NSubstitute handles `async`/`ValueTask` return types more cleanly.

### 9.1 ExpressionEvaluatorTests

Cover all expression forms in §6.2. Key cases:

| Test | Expression | Input | Expected |
|---|---|---|---|
| Whole-value object passthrough | `{{input}}` | `{"a":1}` | `JsonElement` (object) |
| Nested property access | `{{input.a.b}}` | `{"a":{"b":42}}` | `42` (number) |
| String interpolation | `"Order {{input.id}}"` | `{"id":"X1"}` | `"Order X1"` |
| Loop item | `{{$item.name}}` | item=`{"name":"foo"}` | `"foo"` |
| Loop index | `{{$index}}` | index=2 | `2` |
| Condition: true | `{{input.total > 10}}` | `{"total":15}` | `true` |
| Condition: false | `{{input.total > 10}}` | `{"total":5}` | `false` |
| Condition: equality | `{{input.region == "EU"}}` | `{"region":"EU"}` | `true` |
| Condition: AND | `{{input.a > 0 && input.b != null}}` | `{"a":1,"b":"x"}` | `true` |
| Condition: missing property | `{{input.missing > 0}}` | `{}` | `false` (null is falsy) |
| Built-in instanceId | `{{orchestration.instanceId}}` | n/a | the context's instance ID string |
| Step output reference | `{{stepA.result}}` | stepA output = `{"result":99}` | `99` |

### 9.2 WorkflowDefinitionLoaderTests

| Test | Input YAML | Expected |
|---|---|---|
| Minimal activity step | activity + name only | `StepType.Activity`, correct name |
| Implicit type from `activity` field | no explicit `type` | inferred `Activity` |
| All step type parsing | one YAML per type | correct `StepType` and fields |
| Retry policy parsing | retry block present | `RetryPolicy` with correct values |
| ISO duration parsing | `PT5S`, `P7D`, `PT1H30M` | correct `TimeSpan` |
| Invalid type value | `type: unknown` | `WorkflowDefinitionException` |
| Foreach without source | foreach, no source | `WorkflowDefinitionException` |
| Foreach with both activity+workflow | both present | `WorkflowDefinitionException` |
| Valid `on-timeout` values | `fail`, `continue` | parses correctly |
| Invalid `on-timeout` value | `escalate` | `WorkflowDefinitionException` |

### 9.3 WorkflowRunnerTests

Mock `TaskOrchestrationContext` with NSubstitute. Set up `context.Name` to return a workflow name, `context.GetInput<JsonElement>()` to return test input, and `context.CallActivityAsync<JsonElement>()` to return test results.

| Test | Description |
|---|---|
| Single activity step | Calls correct activity name with resolved input |
| Activity with output | Step result stored in `WorkflowExecutionContext` and accessible in next step's input |
| Activity with condition=false | Activity is NOT called (step skipped) |
| Activity with condition=true | Activity IS called |
| Activity retry policy | `CallActivityAsync` called with `TaskOptions` containing `TaskRetryPolicy` |
| Foreach over activity | `CallActivityAsync` called N times (once per item); results collected in order |
| Foreach instance IDs — YAML prescribed | `instanceId: "{{$item.id}}"` → ID suffix is item's id field |
| Foreach instance IDs — default | no `instanceId` field → ID suffix is `context.NewGuid()` |
| Parallel block | All child steps called; results merged into parent context |
| WaitForEvent — event fires | Returns event payload; timer cancelled |
| WaitForEvent — timeout fail | `WorkflowTimeoutException` thrown |
| WaitForEvent — timeout continue | Step output is null; next step runs |
| Switch — matching case | Correct case steps executed |
| Switch — default case | Default steps executed when no key matches |
| Switch — no match, no default | No steps executed; no error |
| Sub-orchestration | `CallSubOrchestratorAsync` called with correct workflow name and instance ID |

---

## 10. Phased Build Order

Execute in this order. Each phase should compile and all tests should pass before starting the next.

1. **Phase 1 — Scaffold**: `.slnx`, all three `.csproj` files, `Program.cs` in TestApp, empty class stubs (models only, no logic). `dotnet build` green.
2. **Phase 2 — YAML Model + Loader**: `WorkflowDefinition`, `StepDefinition`, `WorkflowDefinitionLoader`. Write and pass `WorkflowDefinitionLoaderTests`. No engine logic yet.
3. **Phase 3 — Expression Evaluator**: `ExpressionEvaluator` + `WorkflowExecutionContext`. Write and pass `ExpressionEvaluatorTests`.
4. **Phase 4 — WorkflowRunner**: Full runner with all step types. Write and pass `WorkflowRunnerTests`.
5. **Phase 5 — DI + Extension Methods**: `ServiceCollectionExtensions`, `OrchestrationContextExtensions`, `IWorkflowDefinitionRegistry`. Wire into `WorkflowRunner`.
6. **Phase 6 — TestApp**: Activities, orchestrator stubs, YAML files, `Program.cs` DI wiring. App should start locally without errors (`func start`).
7. **Phase 7 — Polish**: Package metadata in `.csproj`, XML doc comments on all public API types, validate ISO duration edge cases.

---

## 11. Out of Scope for v1

- Source generator for orchestrator stubs
- Python or JavaScript support  
- HTTP trigger generation
- Visual designer / graph view
- `$parent` access pattern (accessing parent orchestration's context from a sub-orchestration)
- Dynamic workflow name in `RunWorkflowAsync` (v1 always uses `context.Name`)
- Durable Entities integration
- Signal/raise-event helper activity (could be a convenience in v2)
