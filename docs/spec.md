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

In the isolated worker model, static methods cannot receive constructor-injected services. The library's `GenericOrchestrator` and `GenericSubOrchestrator` are instance classes that receive `IWorkflowDefinitionRegistry` via constructor injection and call `RunWorkflowAsync` internally. Consumers never write an orchestrator class.

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
- `continue` — store **explicit null** under the step's output name and proceed. The null is always materialized (not a missing key), so `{{approval == null}}` in a downstream condition evaluates correctly regardless of whether the step ran sequentially or inside a parallel branch.

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

The workflow name is the YAML **filename without extension with any `-vN` suffix stripped**, case-preserved. For example, `OrderFulfillment-v2.yaml` resolves to base name `OrderFulfillment`. The `workflow.name` field inside the YAML is optional metadata (used for logging only).

The registry key for each workflow is `"{baseName}:{version}"` where `version` comes from the `workflow.version:` field in the YAML (default: `1`). For example, `OrderFulfillment-v2.yaml` with `version: 2` registers as `"OrderFulfillment:2"`.

The `GenericOrchestrator` reads the workflow name from the `__workflow` key in the Durable instance input envelope — the key always stores the versioned name (e.g., `"OrderFulfillment:2"`) — there are no per-workflow `[Function]` attributes for consumers to maintain.

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
      PollerInput.cs
      DeclarativePollerOrchestrator.cs
      LoopInput.cs
      DeclarativeLoopOrchestrator.cs
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

### 4.4 Activity Function Pattern

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

### 4.5 Trigger Function Pattern

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
  version: <int>      # optional; positive integer; defaults to 1; registry key is "{baseName}:{version}"
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
5. `type` must be one of: `activity`, `sub-orchestration`, `foreach`, `parallel`, `wait-for-event`, `switch`, `poll`, `trigger-and-wait`, `loop`. Any other value is a `WorkflowDefinitionException`.

### 5.4 Activity Step

```yaml
- name: ValidateOrder          # optional
  type: activity
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
  workflow: OrderValidation     # required; must match a registered workflow base name
  version: 1                   # optional; positive integer; pins to exact version at dispatch time
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
- `version` is optional. If omitted, the sub-orchestration resolves to the current latest registered version at the moment the step first executes. If present, the versioned name `"{workflow}:{version}"` must exist in the registry at execution time.

### 5.6 Foreach Step

```yaml
- name: ReserveInventory
  type: foreach
  source: "{{input.lineItems}}"  # required; must evaluate to a JSON array
  activity: ReserveItemActivity  # mutually exclusive with workflow
  # workflow: SomeWorkflow       # alternative: foreach over sub-orchestration
  version: 1                     # optional; positive integer; pins workflow to exact version
                                 # only meaningful when workflow is set; silently ignored for activity
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
- `version` is optional (applies only when `workflow` is set). If omitted, resolves to the current latest registered version. If present, the versioned name `"{workflow}:{version}"` must exist in the registry at execution time.

### 5.7 Parallel Step

A parallel block is a **fan-out/fan-in** construct. All branches launch concurrently from the same starting state and the block waits for all of them before continuing. Results are collected into a single aggregate object.

```yaml
- name: Finalize
  type: parallel
  output: finalize          # optional; stores the aggregate in parent context
  condition: "{{...}}"      # optional; evaluated once before launching branches
  steps:
    - name: SendConfirmation
      activity: SendConfirmationEmailActivity
      input: "{{input.customerEmail}}"
    - name: UpdateLedger
      type: sub-orchestration
      workflow: LedgerUpdate
      input: "{{fulfillmentResults}}"
    - name: NotifyWarehouse
      type: wait-for-event
      event: WarehouseAcknowledged
      timeout: PT1H

- name: Audit
  activity: AuditActivity
  input:
    confirmation: "{{finalize.SendConfirmation}}"
    confirmationId: "{{finalize.SendConfirmation.confirmationId}}"
    approvedBy: "{{finalize.SendConfirmation.approvedBy}}"
```

**Semantics:**

- Each branch starts from a **snapshot** of the parent `WorkflowExecutionContext` at block-start. Branches cannot observe each other's writes while the block is running.
- The block's `output:` field names where the aggregate object is stored in the parent context. If omitted, branches are awaited but results are not exposed to subsequent steps.
- The aggregate is a JSON object keyed by **child step name**. Every named child step contributes an entry. **Null entries are always included** — they are never omitted.
- `output:` on a child step inside a parallel block is a **load-time error** (`WorkflowDefinitionException`). Branch results are always keyed by step name; there is no per-child output alias.
- Child step names must be unique within the block.
- If any branch fails, the parallel block fails.

**Branch result by step type** (value in the aggregate under the child's step name):

| Child step type | Branch result |
|---|---|
| `activity` | Return value of the activity call |
| `sub-orchestration` | Return value of the sub-orchestration |
| `foreach` | Array of all iteration results |
| `wait-for-event` | Event payload; `null` if `on-timeout: continue` fires |
| `trigger-and-wait` | Event payload; `null` if `on-timeout: continue` fires |
| `poll` | Last activity result satisfying `until`; `null` if `on-timeout: continue` fires |
| `loop` | Value of the loop's `output` variable on break; `null` if `on-timeout: continue` fires |
| `switch` | `null` — switch routes execution and has no return value |
| `parallel` (nested) | The nested block's aggregate object |
| Any step with `condition: false` | `null` — the step was skipped |

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

**If-else mode** — use a boolean expression in `on:` and match on `"true"`/`"false"`:

```yaml
- name: HandleResult
  type: switch
  on: "{{stepResult.status == 'Succeeded'}}"
  cases:
    "true":
      - name: OnSuccess
        activity: HandleSuccessActivity
        input:
          status: "{{stepResult.status}}"
          message: "{{stepResult.message}}"
    "false":
      - name: OnFailure
        activity: HandleFailureActivity
        input:
          status: "{{stepResult.status}}"
```

Boolean expressions always produce the lowercase strings `"true"` or `"false"` — write case keys in lowercase accordingly.

- Case keys are strings. Comparison against the evaluated `on` expression is **string comparison, case-sensitive** (convert number and boolean results to string before comparing; booleans become `"true"` or `"false"`).
- The `default` key is reserved and matched last.
- If no case matches and there is no `default`, the switch step is a no-op (no error).
- Steps within a case inherit the parent `WorkflowExecutionContext` and may write named outputs into it.

### 5.10 Poll Step

```yaml
- name: WaitForCompletion
  type: poll
  activity: CheckStatusActivity
  input: "{{input.correlationId}}"
  output: statusResult
  until: "{{statusResult.status == 'Complete'}}"
  delay: PT100M
  timeout: PT30D
  on-timeout: fail        # fail (default) | continue
  condition: "{{...}}"   # optional
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `activity` | string | Yes | Activity function name called on each iteration |
| `input` | expression or object | No | Resolved once in parent context; same value used for every iteration |
| `output` | string | Yes | Variable name bound to the latest activity result; used in `until` expression and stored in parent context on success |
| `until` | expression | Yes | Boolean expression evaluated after each activity call; must reference `output` name |
| `delay` | duration | Yes | ISO 8601 duration; sleep between failed iterations (e.g. `PT100M`, `PT30S`) |
| `timeout` | duration | No | ISO 8601 wall-clock bound on the entire polling loop; omit to poll indefinitely |
| `on-timeout` | string | No | `fail` (default) — throw `WorkflowTimeoutException`; `continue` — store `null` and proceed |
| `condition` | expression | No | If false, the step is skipped and no poller sub-orchestration is started |

**Scope constraint on `until`:** The `until` expression runs inside the `DeclarativeWorkflowPoller` sub-orchestration. It has no access to the parent workflow's step outputs. Only the step's own `output` name (the latest activity result) and `orchestration.instanceId` / `orchestration.parentInstanceId` are available.

**Implementation:** `type: poll` dispatches to a built-in sub-orchestration named `DeclarativeWorkflowPoller` that ships inside the library assembly. The Functions runtime scans the library assembly and registers this orchestration automatically. `ContinueAsNew` is called after each failed iteration with the original `StartedAt` preserved, so the wall-clock timeout is correctly enforced across restarts. History is reset on each `ContinueAsNew` call, keeping it bounded regardless of how many iterations run.

### 5.11 Send-and-Wait Step

```yaml
- name: ProcessOrder
  type: trigger-and-wait
  activity: SendOrderToProcessorActivity   # required; sends the trigger message
  input: "{{input.order}}"                 # optional; input to the activity
  event: OrderProcessed                    # required; external event name
  timeout: PT60M                           # optional; ISO 8601; omit to wait indefinitely
  on-timeout: fail                         # optional; fail (default) | continue
  output: processingResult                 # optional; receives JsonElement of event payload
  condition: "{{...}}"                     # optional
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `activity` | string | Yes | Activity function name. Expected to be fast (fire-and-forget send) |
| `input` | expression or object | No | Resolved in the parent context before the step executes |
| `event` | string | Yes | External event name passed to `RaiseEventAsync` by the callback |
| `timeout` | duration | No | ISO 8601 wall-clock budget for the external callback; omit to wait indefinitely |
| `on-timeout` | string | No | `fail` (default) — throw `WorkflowTimeoutException`; `continue` — store `null` and proceed |
| `output` | string | No | Variable name for the event payload |
| `condition` | expression | No | If false, neither the activity nor the event wait is started |

**Critical ordering guarantee:** The external event listener is registered **before** `CallActivityAsync` is called. This prevents a race condition where a fast downstream system processes the trigger message and raises the callback event before the orchestrator has expressed interest in it. This race has been observed in production with Azure Service Bus and confirmed by Microsoft — the sequential `activity` → `wait-for-event` pattern is unsafe when the activity is a message send.

**Execution semantics:**

1. `context.WaitForExternalEvent<JsonElement>(event)` — registered first
2. Timer created (if `timeout` is set)
3. `context.CallActivityAsync<JsonElement>(activity, resolvedInput)` — called second
4. `Task.WhenAny(eventTask, timerTask)` races the callback against the timeout
5. `Task.WhenAll(winningTask, activityTask)` — the activity is always awaited before the step completes; it is expected to finish quickly (it is a send, not the work itself)
6. If the event wins: timer cancelled; event payload stored under `output`
7. If the timer wins: `on-timeout: fail` throws `WorkflowTimeoutException`; `on-timeout: continue` stores `null`
8. If no `timeout`: the activity is awaited, then the event is awaited indefinitely

### 5.12 Loop Step

```yaml
- name: WaitForSignal
  type: loop
  max-duration: P30D        # required; ISO 8601 overall wall-clock timeout
  delay: PT1H               # required; ISO 8601 sleep between iterations
  break-when: "{{signalResult.status == 'success'}}"  # required; boolean expression
  on-timeout: continue      # optional; fail (default) | continue
  output: signalResult      # required; names a variable produced by an inner step
  condition: "{{...}}"      # optional
  steps:                    # required; sequence of steps (any type)
    - name: AttemptSignal
      type: trigger-and-wait
      activity: SendSignalActivity
      input:
        correlationId: "{{orchestration.instanceId}}"
      event: SignalReceived
      timeout: PT5M
      on-timeout: continue
      output: signalResult
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `steps` | sequence | Yes | One or more steps of any type; executed sequentially each iteration |
| `break-when` | expression | Yes | Boolean expression evaluated after each iteration against the inner execution context; exits the loop when true |
| `output` | string | Yes | Names a variable that must be produced by one of the inner steps; its last known value is returned on break or timeout-continue |
| `delay` | duration | Yes | ISO 8601 sleep between iterations (e.g. `PT1H`, `PT30M`) |
| `max-duration` | duration | Yes | ISO 8601 wall-clock bound on the entire loop |
| `on-timeout` | string | No | `fail` (default) — throw `WorkflowTimeoutException`; `continue` — store `null` and proceed |
| `condition` | expression | No | If false, the loop is skipped entirely and no sub-orchestration is started |

**Semantics:**

- The inner steps run sequentially each iteration in their own `WorkflowExecutionContext` (seeded with the outputs carried forward from the previous iteration, so `break-when` can reference any inner step output by name).
- After all inner steps complete, `break-when` is evaluated. If true, the loop exits and the value stored under `output` in the inner context is returned to the parent.
- If `break-when` is false and `max-duration` has not elapsed, the engine sleeps `delay` then calls `ContinueAsNew`, resetting history to keep it bounded.
- `output` is required (unlike `poll`) because `break-when` must reference at least one inner step result — and the loop needs to know what to return to the parent on exit.
- Unlike `poll`'s `until` expression (which runs inside the poller with access only to the step's own activity result), `break-when` has access to all inner step outputs from the current iteration — useful when the loop body has multiple steps and the exit condition combines their results.

**Scope constraint on `break-when`:** Evaluated inside `DeclarativeWorkflowLoop`. Has access to the inner step outputs carried in `LoopInput` (updated after each iteration) and `orchestration.instanceId` / `orchestration.parentInstanceId`. Does not have access to the parent workflow's step outputs.

**Implementation:** `type: loop` dispatches to a built-in sub-orchestration named `DeclarativeWorkflowLoop` that ships inside the library assembly. The parent orchestration serializes the loop's step definitions to JSON and passes them along with `BreakWhen`, `Delay`, `MaxDuration`, `OnTimeout`, `OutputName`, `StartedAt`, and the last iteration's outputs (as a `Dictionary<string, JsonElement>`) in a `LoopInput` envelope. `ContinueAsNew` preserves the original `StartedAt` so the wall-clock timeout is correctly enforced across restarts.

### 5.13 Complete Example: `OrderFulfillment.yaml`

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
      output: finalize
      steps:
        - name: SendConfirmation
          activity: SendConfirmationEmailActivity
          input: "{{input.customerEmail}}"
        - name: UpdateInventory
          activity: UpdateInventoryActivity
          input: "{{fulfillmentResults}}"
```

### 5.14 Complete Example: `FulfillLineItem.yaml`

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

**Condition-only forms** (only valid in `condition`, `until`, and `switch.on` fields):

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
    IReadOnlyCollection<string> WorkflowNames { get; }  // all versioned keys, e.g. "OrderFulfillment:2"
    string ResolveVersionedName(string workflowName);   // unversioned → "{name}:{latest}"; versioned → passthrough
}

// Engine-internal only — not visible to library consumers
internal interface IWorkflowDefinitionRegistryInternal : IWorkflowDefinitionRegistry
{
    WorkflowDefinition Get(string workflowName);         // accepts versioned or unversioned name
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
    Switch,
    Poll,
    TriggerAndWait,
    Loop,
}

// Models/AppRetryPolicy.cs
internal sealed class AppRetryPolicy
{
    public int MaxAttempts { get; init; }
    public string FirstRetryInterval { get; init; } = "PT1S";  // ISO 8601
    public string? MaxRetryInterval { get; init; }
    public double BackoffCoefficient { get; init; } = 1.0;

    public RetryPolicy ToSdkRetryPolicy();  // returns Microsoft.DurableTask.RetryPolicy
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
    public AppRetryPolicy? Retry { get; init; }

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

    // Poll
    public string? Until { get; init; }    // required for Poll; boolean expression
    public string? Delay { get; init; }    // required for Poll and Loop; ISO 8601 duration

    // Loop
    public string? BreakWhen { get; init; }  // required for Loop; boolean expression
    // MaxDuration reuses Timeout field (ISO 8601); Steps reuses the Steps field from Parallel

    // Sub-orchestration / Foreach: explicit version pin; null = resolve to latest at dispatch time
    public string? WorkflowVersion { get; init; }
}

// Models/WorkflowDefinition.cs
internal sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;   // base name from filename (no -vN suffix)
    public string? DisplayName { get; init; }            // from workflow.name in YAML; metadata only
    public int Version { get; init; } = 1;              // from workflow.version in YAML; default 1
    public string VersionedName => $"{Name}:{Version}"; // computed registry key, e.g. "OrderFulfillment:2"
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

    // Creates a child scope for foreach iterations carrying $item and $index
    public WorkflowExecutionContext CreateIterationScope(JsonElement item, int index);

    // Creates an isolated snapshot scope for a parallel branch; branches cannot observe each other's writes
    public WorkflowExecutionContext CreateParallelBranchScope();
}

// Engine/LoopInput.cs — see §8.12 for full type definition and DeclarativeLoopOrchestrator logic

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
internal static class WorkflowRunner
{
    public static Task RunAsync(
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
1. Cast registry to IWorkflowDefinitionRegistryInternal (throws if wrong implementation)
2. internalRegistry.TryGet(context.Name, out definition)  // throws WorkflowDefinitionException if not found
3. inputJson = context.GetInput<JsonElement>()  // default(JsonElement) if no input
4. execCtx = new WorkflowExecutionContext(inputJson, context)
5. await WorkflowRunner.RunAsync(context, definition, execCtx)  // WorkflowRunner is static
```

### 8.3 `WorkflowRunner.RunAsync` — Step Dispatch

Each dispatch method accepts an optional `outputNameOverride` used by `RunParallel` to store results under the child's step name instead of `step.Output`. Normal sequential execution passes no override.

Output storage happens **inside** each dispatch method using `effectiveOutput = outputNameOverride ?? step.Output`. There is no post-dispatch storage step.

```
For each step in definition.Steps:
  1. If step.Condition is set:
       if !ExpressionEvaluator.EvaluateBool(step.Condition, execCtx): skip step
  2. Dispatch by step.Type (each method handles its own output storage):
       Activity          → RunActivity(context, step, execCtx)
       SubOrchestration  → RunSubOrchestration(context, step, execCtx)
       Foreach           → RunForeach(context, step, execCtx)
       Parallel          → RunParallel(context, step, execCtx)
       WaitForEvent      → RunWaitForEvent(context, step, execCtx)
       Switch            → RunSwitch(context, step, execCtx)
       TriggerAndWait       → RunTriggerAndWait(context, step, execCtx)
       Loop                 → RunLoop(context, step, execCtx)
```

### 8.4 Activity Dispatch

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)
options = step.Retry != null ? TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy()) : null
result = await context.CallActivityAsync<JsonElement>(step.ActivityName, resolvedInput, options)
return result
```

### 8.5 Sub-Orchestration Dispatch

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)
instanceIdSuffix = step.InstanceId != null
    ? ExpressionEvaluator.Evaluate(step.InstanceId, execCtx)?.ToString()
    : context.NewGuid().ToString()
instanceId = $"{context.InstanceId}:{step.Name}:{instanceIdSuffix}"
taskOptions = step.Retry != null ? TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy()) : null
options = new SubOrchestrationOptions(taskOptions, instanceId)  // (TaskOptions?, string) ctor
result = await context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName, resolvedInput, options)
effectiveOutput = outputNameOverride ?? step.Output
if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, result)
```

`SubOrchestrationOptions` constructor (verified via reflection on `Microsoft.DurableTask.Abstractions` 1.24.1): `(TaskOptions? options, string instanceId)`. There is NO record-style `(Retry: ..., InstanceId: ...)` named-parameter form — it does not compile. Pass null `TaskOptions` when no retry policy.

### 8.6 Foreach Dispatch

```
source = ExpressionEvaluator.Evaluate(step.Source, execCtx)  // must be JsonElement array
items = source.EnumerateArray().ToList()

tasks = items.Select((item, index) =>
{
    iterCtx = execCtx.CreateIterationScope(item, index)
    resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, iterCtx)

    if step.ActivityName != null:
        activityOptions = step.Retry != null ? TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy()) : null
        return context.CallActivityAsync<JsonElement>(step.ActivityName, resolvedInput, activityOptions)
    else:
        instanceIdSuffix = step.InstanceId != null
            ? ExpressionEvaluator.Evaluate(step.InstanceId, iterCtx)?.ToString()
            : context.NewGuid().ToString()
        instanceId = $"{context.InstanceId}:{step.Name}:{instanceIdSuffix}"
        taskOptions = step.Retry != null ? TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy()) : null
        options = new SubOrchestrationOptions(taskOptions, instanceId)
        return context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName, resolvedInput, options)
})

results = await Task.WhenAll(tasks)
return results  // array in source order; stored as object[] in execCtx
```

If `source` does not evaluate to a JSON array: throw `WorkflowExpressionException`.

### 8.7 Parallel Dispatch

Each branch runs against an isolated snapshot of the parent context. The step name is the implicit output key — `output:` on a child step is a validation error caught by the loader, never seen here.

```
// Fork one snapshot context per branch
branchScopes = step.Steps.Select(_ => execCtx.CreateParallelBranchScope())

// Dispatch each child with its step name as the output key override
tasks = step.Steps.Select((child, i) =>
    ExecuteStep(context, child, branchScopes[i], outputNameOverride: child.Name)
)
await Task.WhenAll(tasks)

// Build aggregate only if the block declared an output
if step.Output != null:
    aggregate = {}
    for (child, branchScope) in zip(step.Steps, branchScopes):
        if child.Name != null:
            aggregate[child.Name] = branchScope.HasOutput(child.Name)
                ? branchScope.GetOutput(child.Name)
                : null   // null for switch, skipped condition, timeout-continue — always present
    execCtx.SetOutput(step.Output, JsonSerializer.SerializeToElement(aggregate))
```

`outputNameOverride` propagates through `ExecuteStep` to whichever dispatch method handles the child (Activity, SubOrchestration, Foreach, WaitForEvent, nested Parallel). Each method uses `effectiveOutput = outputNameOverride ?? step.Output` when deciding where to store its result. Switch does not store a result regardless.

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

### 8.10 TriggerAndWait Dispatch

The external event listener **must** be registered before the activity is called. This ordering prevents the race condition where a fast downstream system raises the callback event before the orchestrator has expressed interest in it.

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)

// Step 1: register event interest FIRST
using cts = new CancellationTokenSource()
eventTask = context.WaitForExternalEvent<JsonElement>(step.EventName)

// Step 2: create timer (if timeout specified)
timerTask = step.Timeout != null
    ? context.CreateTimer(
        context.CurrentUtcDateTime + ParseIso8601Duration(step.Timeout),
        cts.Token)
    : null

// Step 3: call activity AFTER event listener is set up
activityTask = context.CallActivityAsync<JsonElement>(step.ActivityName, resolvedInput)

// Step 4: race event vs timer; always await activity
if timerTask != null:
    winningTask = await Task.WhenAny(eventTask, timerTask)
    await Task.WhenAll(winningTask, activityTask)  // activity is always awaited

    if winningTask == timerTask:
        if step.OnTimeout == "fail":
            throw new WorkflowTimeoutException(step.Name ?? step.EventName, step.Timeout)
        else:  // "continue"
            effectiveOutput = outputNameOverride ?? step.Output
            if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, null)
            return null
    else:
        cts.Cancel()
        result = await eventTask  // already completed
        effectiveOutput = outputNameOverride ?? step.Output
        if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, result)
        return result
else:
    // No timeout: await activity, then wait indefinitely for event
    await activityTask
    result = await eventTask
    effectiveOutput = outputNameOverride ?? step.Output
    if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, result)
    return result
```

### 8.11 Poll Dispatch

The poll step is dispatched as a call to the built-in `DeclarativeWorkflowPoller` sub-orchestration. The parent workflow resolves the activity input once (in its own `WorkflowExecutionContext`), then hands off a frozen `PollerInput` to the sub-orchestration.

**In `WorkflowRunner` (parent orchestration):**

```
resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx)
activityInputJson = JsonSerializer.SerializeToElement(resolvedInput)

pollerInput = new PollerInput
{
    ActivityName    = step.ActivityName,
    ActivityInput   = activityInputJson,
    OutputName      = step.Output,             // required; validated at load time
    UntilExpression = step.Until,
    Delay           = step.Delay,
    Timeout         = step.Timeout,            // null if omitted
    OnTimeout       = step.OnTimeout,          // "fail" | "continue"
    StartedAt       = context.CurrentUtcDateTime
}

instanceId = $"{context.InstanceId}:{step.Name ?? step.ActivityName}:poller"
options = new SubOrchestrationOptions(null, instanceId)
result = await context.CallSubOrchestratorAsync<JsonElement>("DeclarativeWorkflowPoller", pollerInput, options)

effectiveOutput = outputNameOverride ?? step.Output
if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, result)
```

**`PollerInput` internal type** (add to `Engine/PollerInput.cs`):

```csharp
internal sealed class PollerInput
{
    public string ActivityName { get; init; } = string.Empty;
    public JsonElement? ActivityInput { get; init; }
    public string OutputName { get; init; } = string.Empty;    // variable name for until expression
    public string UntilExpression { get; init; } = string.Empty;
    public string Delay { get; init; } = string.Empty;         // ISO 8601
    public string? Timeout { get; init; }                       // ISO 8601; null = no timeout
    public string OnTimeout { get; init; } = "fail";
    public DateTimeOffset StartedAt { get; init; }              // fixed at first iteration; preserved across ContinueAsNew
}
```

**`DeclarativeWorkflowPoller` orchestration** (add to `Engine/DeclarativePollerOrchestrator.cs`):

The class lives in the library assembly. The Functions runtime scans the library assembly alongside the consumer's app assembly and registers the function automatically — no consumer-side registration required.

```
input = context.GetInput<PollerInput>()

// Call the activity for this iteration
result = await context.CallActivityAsync<JsonElement>(input.ActivityName, input.ActivityInput)

// Evaluate the until condition against a minimal context containing only the activity result
miniCtx = new WorkflowExecutionContext(default(JsonElement), context)
miniCtx.SetOutput(input.OutputName, result)
conditionMet = ExpressionEvaluator.EvaluateBool(input.UntilExpression, miniCtx)

if conditionMet:
    return result   // completes the poller; parent stores under output name

// Check wall-clock timeout before sleeping
if input.Timeout != null:
    elapsed = context.CurrentUtcDateTime - input.StartedAt
    if elapsed >= ParseIso8601Duration(input.Timeout):
        if input.OnTimeout == "fail":
            throw new WorkflowTimeoutException(input.ActivityName, input.Timeout)
        else:  // "continue"
            return null   // parent stores null under output name; matches wait-for-event semantics

// Sleep the configured delay, then restart with ContinueAsNew
// StartedAt is preserved in input — it always measures wall-clock from the first iteration
await context.CreateTimer(
    context.CurrentUtcDateTime + ParseIso8601Duration(input.Delay),
    CancellationToken.None)

context.ContinueAsNew(input)
// No return needed; ContinueAsNew schedules the restart and the current execution ends
```

### 8.12 Loop Dispatch

The loop step dispatches to the built-in `DeclarativeWorkflowLoop` sub-orchestration. The parent orchestration serializes the loop's step definitions to JSON and passes a `LoopInput` envelope. The loop orchestrator runs the inner steps on each iteration using its own `WorkflowExecutionContext`, evaluates `break-when`, and either returns or calls `ContinueAsNew`.

**In `WorkflowRunner` (parent orchestration):**

```
stepsJson = JsonSerializer.Serialize(step.Steps)   // serialize IReadOnlyList<StepDefinition>

loopInput = new LoopInput
{
    StepsJson        = stepsJson,
    BreakWhen        = step.BreakWhen,
    OutputName       = step.Output,
    Delay            = step.Delay,
    MaxDuration      = step.Timeout,      // max-duration maps to Timeout field
    OnTimeout        = step.OnTimeout,    // "fail" | "continue"
    StartedAt        = context.CurrentUtcDateTime,
    PreviousOutputs  = new Dictionary<string, JsonElement>()  // empty on first call
}

instanceId = $"{context.InstanceId}:{step.Name ?? "loop"}:loop"
options = new SubOrchestrationOptions(null, instanceId)
result = await context.CallSubOrchestratorAsync<JsonElement?>("DeclarativeWorkflowLoop", loopInput, options)

effectiveOutput = outputNameOverride ?? step.Output
if effectiveOutput != null: execCtx.SetOutput(effectiveOutput, result)
```

**`LoopInput` internal type** (add to `Engine/LoopInput.cs`):

```csharp
internal sealed class LoopInput
{
    public string StepsJson { get; init; } = string.Empty;     // JSON-serialized StepDefinition[]
    public string BreakWhen { get; init; } = string.Empty;     // boolean expression
    public string OutputName { get; init; } = string.Empty;    // variable name for break-when and return value
    public string Delay { get; init; } = string.Empty;         // ISO 8601
    public string? MaxDuration { get; init; }                  // ISO 8601; null = no timeout
    public string OnTimeout { get; init; } = "fail";
    public DateTimeOffset StartedAt { get; init; }             // fixed at first iteration; preserved across ContinueAsNew
    public Dictionary<string, JsonElement> PreviousOutputs { get; init; } = [];  // inner step outputs from last iteration
}
```

**`StepDefinition` serialization:** `StepDefinition` must be JSON-serializable (add `[JsonInclude]` or ensure all `init` properties serialize correctly via `System.Text.Json`). The nested `Steps` (for `parallel` and `loop`) and `Cases` (for `switch`) must round-trip correctly.

**`DeclarativeWorkflowLoop` orchestration** (add to `Engine/DeclarativeLoopOrchestrator.cs`):

```
input = context.GetInput<LoopInput>()

// Deserialize step definitions
steps = WorkflowDefinitionLoader.DeserializeSteps(input.StepsJson)

// Seed the inner context with outputs carried from the previous iteration
innerCtx = new WorkflowExecutionContext(default(JsonElement), context)
foreach (name, value) in input.PreviousOutputs:
    innerCtx.SetOutput(name, value)

// Run all inner steps for this iteration
await WorkflowRunner.RunAsync(context, new WorkflowDefinition { Steps = steps }, innerCtx)

// Evaluate the break condition against the inner context
conditionMet = ExpressionEvaluator.EvaluateBool(input.BreakWhen, innerCtx)

if conditionMet:
    // Return the named output to the parent; null if the step never ran (e.g. condition:false)
    return innerCtx.HasOutput(input.OutputName) ? innerCtx.GetOutput(input.OutputName) : null

// Check wall-clock timeout before sleeping
if input.MaxDuration != null:
    elapsed = context.CurrentUtcDateTime - input.StartedAt
    if elapsed >= ParseIso8601Duration(input.MaxDuration):
        if input.OnTimeout == "fail":
            throw new WorkflowTimeoutException("loop", input.MaxDuration)
        else:  // "continue"
            return null

// Carry all inner outputs forward for the next iteration
nextInput = input with
{
    PreviousOutputs = innerCtx.Outputs
        .Where(kvp => kvp.Value is JsonElement)
        .ToDictionary(kvp => kvp.Key, kvp => (JsonElement)kvp.Value)
}

// Sleep, then restart
await context.CreateTimer(
    context.CurrentUtcDateTime + ParseIso8601Duration(input.Delay),
    CancellationToken.None)

context.ContinueAsNew(nextInput)
```

**`WorkflowDefinitionLoader.DeserializeSteps`** — add as a new `internal static` method that accepts a JSON string and returns `IReadOnlyList<StepDefinition>`. This is the inverse of the JSON serialization done in the parent orchestration dispatch.

### 8.13 YAML Parsing (`WorkflowDefinitionLoader`)

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
11. A child step inside a `parallel` block must not declare `output:`. Throw `WorkflowDefinitionException` at load time with a message explaining that branch results are keyed by step name and collected via the block's own `output:` field.
12. `poll` step must have `activity`.
13. `poll` step must have `until`.
14. `poll` step must have `delay`.
15. `poll` step must have `output` (required so the `until` expression has a variable name to reference).
16. `poll` step `on-timeout` must be `fail` or `continue` if present.
17. `trigger-and-wait` step must have `activity`.
18. `trigger-and-wait` step must have `event`.
19. `trigger-and-wait` step `on-timeout` must be `fail` or `continue` if present.
20. `loop` step must have `steps` as a non-empty sequence.
21. `loop` step must have `break-when`.
22. `loop` step must have `delay`.
23. `loop` step must have `max-duration`.
24. `loop` step must have `output` (required so `break-when` has a variable name to reference and so the parent context knows what to store).
25. `loop` step `on-timeout` must be `fail` or `continue` if present.
26. A child step inside a `loop` body must not itself be a `loop` step that references outputs not available in the loop's inner context. (Advisory — not a hard validation error, but document the scope constraint.)

ISO 8601 duration parsing: use a helper that converts `PT5S` → `TimeSpan.FromSeconds(5)`, `P7D` → `TimeSpan.FromDays(7)`, etc. Standard .NET does not parse ISO 8601 durations natively; implement a minimal parser (only `P`, `D`, `H`, `M`, `S` components needed).

### 8.14 Determinism Constraints

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
| Condition: missing property in comparison | `{{input.missing > 0}}` | `{}` | `false` (null is falsy) |
| Condition: bare path — unset variable | `EvaluateBool("{{approval}}", ctx)` where `approval` not in context | `false` — must NOT throw |
| Condition: bare path — missing property | `EvaluateBool("{{input.optionalFlag}}", ctx)` where field absent | `false` — must NOT throw |
| Built-in instanceId | `{{orchestration.instanceId}}` | n/a | the context's instance ID string |
| Step output reference | `{{stepA.result}}` | stepA output = `{"result":99}` | `99` |
| Input expression — unset variable throws | `Evaluate("{{missing}}", ctx)` where `missing` not in context | `WorkflowExpressionException` thrown (non-condition path must not swallow errors) |

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
| Parallel child with `output:` | child step declares `output: foo` inside parallel | `WorkflowDefinitionException` at load time |
| Poll — all required fields | `type: poll` with `activity`, `output`, `until`, `delay` | parses correctly; `StepType.Poll` |
| Poll — missing `until` | `type: poll` without `until` | `WorkflowDefinitionException` |
| Poll — missing `delay` | `type: poll` without `delay` | `WorkflowDefinitionException` |
| Poll — missing `output` | `type: poll` without `output` | `WorkflowDefinitionException` |
| Poll — missing `activity` | `type: poll` without `activity` | `WorkflowDefinitionException` |
| Poll — optional `timeout` absent | `type: poll` without `timeout` | parses; `Timeout` is null |
| TriggerAndWait — all required fields | `type: trigger-and-wait` with `activity` and `event` | parses correctly; `StepType.TriggerAndWait` |
| TriggerAndWait — missing `activity` | `type: trigger-and-wait` without `activity` | `WorkflowDefinitionException` |
| TriggerAndWait — missing `event` | `type: trigger-and-wait` without `event` | `WorkflowDefinitionException` |
| TriggerAndWait — optional `timeout` absent | `type: trigger-and-wait` without `timeout` | parses; `Timeout` is null |
| Loop — all required fields | `type: loop` with `steps`, `break-when`, `delay`, `max-duration`, `output` | parses correctly; `StepType.Loop` |
| Loop — missing `break-when` | `type: loop` without `break-when` | `WorkflowDefinitionException` |
| Loop — missing `delay` | `type: loop` without `delay` | `WorkflowDefinitionException` |
| Loop — missing `max-duration` | `type: loop` without `max-duration` | `WorkflowDefinitionException` |
| Loop — missing `output` | `type: loop` without `output` | `WorkflowDefinitionException` |
| Loop — missing `steps` | `type: loop` without `steps` | `WorkflowDefinitionException` |
| Loop — inner steps parsed recursively | loop body contains `trigger-and-wait` step | inner `StepDefinition` has `StepType.TriggerAndWait` |

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
| Parallel — isolated branches | Branch cannot read a sibling's output; each runs against its own snapshot context |
| Parallel — aggregate output | Block's `output:` receives JSON object keyed by child step name |
| Parallel — null in aggregate | Switch branch and condition-skipped branch appear as null in aggregate, not missing |
| Parallel — nested parallel | Nested block's aggregate is the branch result of the outer parallel entry |
| WaitForEvent — event fires | Returns event payload; timer cancelled |
| WaitForEvent — timeout fail | `WorkflowTimeoutException` thrown |
| WaitForEvent — timeout continue (sequential) | Output stored as explicit null; downstream `{{approval == null}}` is true |
| WaitForEvent — timeout continue (parallel branch) | Null appears in aggregate under branch's step name |
| Switch — matching case | Correct case steps executed |
| Switch — default case | Default steps executed when no key matches |
| Switch — no match, no default | No steps executed; no error |
| Sub-orchestration | `CallSubOrchestratorAsync` called with correct workflow name and instance ID |
| Poll — condition satisfied on first call | `CallSubOrchestratorAsync("DeclarativeWorkflowPoller", ...)` called; result stored under `output` |
| Poll — `PollerInput` fields | `ActivityName`, `OutputName`, `UntilExpression`, `Delay`, `Timeout`, `OnTimeout`, `StartedAt` all correctly populated |
| Poll — input resolved in parent context | Activity input expression referencing parent step output is resolved before `PollerInput` is constructed |
| Poll — instance ID format | Sub-orchestration instance ID is `{parentInstanceId}:{stepName}:poller` |
| Poll — condition=false skips | When step `condition` is false, `DeclarativeWorkflowPoller` is never called |
| TriggerAndWait — event wins | `WaitForExternalEvent` registered before `CallActivityAsync`; event payload stored under `output` |
| TriggerAndWait — listener before activity | Verify call order: `WaitForExternalEvent` is set up before `CallActivityAsync` is invoked |
| TriggerAndWait — timeout fail | `WorkflowTimeoutException` thrown; activity still awaited before exception propagates |
| TriggerAndWait — timeout continue | `null` stored under `output`; activity still awaited; execution proceeds |
| TriggerAndWait — no timeout | Activity awaited; event awaited indefinitely; result stored |
| TriggerAndWait — condition=false skips | Neither `WaitForExternalEvent` nor `CallActivityAsync` is called |
| TriggerAndWait — parallel branch null on timeout-continue | `null` appears in aggregate under branch step name |
| Loop — break on first iteration | `break-when` true after first run; `CallSubOrchestratorAsync("DeclarativeWorkflowLoop", ...)` called; result stored under `output` |
| Loop — `LoopInput` fields | `BreakWhen`, `OutputName`, `Delay`, `MaxDuration`, `OnTimeout`, `StartedAt`, `StepsJson` all correctly populated |
| Loop — inner outputs carried forward | `PreviousOutputs` in `LoopInput` contains outputs from prior iteration so `break-when` can reference them |
| Loop — instance ID format | Sub-orchestration instance ID is `{parentInstanceId}:{stepName}:loop` |
| Loop — timeout fail | `WorkflowTimeoutException` thrown when `max-duration` elapsed |
| Loop — timeout continue | `null` stored under `output`; execution proceeds |
| Loop — condition=false skips | When step `condition` is false, `DeclarativeWorkflowLoop` is never called |
| Loop — parallel branch null on timeout-continue | `null` appears in aggregate under branch step name |

---

## 10. Phased Build Order

Execute in this order. Each phase should compile and all tests should pass before starting the next.

1. **Phase 1 — Scaffold**: `.slnx`, all three `.csproj` files, `Program.cs` in TestApp, empty class stubs (models only, no logic). `dotnet build` green.
2. **Phase 2 — YAML Model + Loader**: `WorkflowDefinition`, `StepDefinition`, `WorkflowDefinitionLoader`. Write and pass `WorkflowDefinitionLoaderTests`. No engine logic yet.
3. **Phase 3 — Expression Evaluator**: `ExpressionEvaluator` + `WorkflowExecutionContext`. Write and pass `ExpressionEvaluatorTests`.
4. **Phase 4 — WorkflowRunner**: Full runner with all step types including `Poll` (dispatch to `DeclarativeWorkflowPoller`), `Loop` (dispatch to `DeclarativeWorkflowLoop`), and their respective orchestrator classes. Write and pass `WorkflowRunnerTests`.
5. **Phase 5 — DI + Extension Methods**: `ServiceCollectionExtensions`, `OrchestrationContextExtensions`, `IWorkflowDefinitionRegistry`. Wire into `WorkflowRunner`.
6. **Phase 6 — TestApp**: Activities, YAML files, `Program.cs` DI wiring. App should start locally without errors (`func start`).
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

---

## 12. References
- [Durable Functions Error Handling - RetryPolicy](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-error-handling?tabs=csharp&pivots=durable-functions)
