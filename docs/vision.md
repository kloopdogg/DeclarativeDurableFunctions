# Vision: Declarative Durable Functions

CROW = Composable Runtime for Orchestrated Workflows

## The Problem

Azure Durable Functions are powerful — stateful, reliable, long-running workflows in a serverless environment. But the orchestrator function code is notoriously painful to write and read. The `await context.CallActivityAsync(...)` boilerplate, the replay constraints, the ceremony around fan-out/fan-in and external events — all of it turns developers off and keeps them from adopting Durable Functions at all.

## The Vision

A NuGet package that puts a declarative YAML layer on top of Durable Functions. Developers still write all their activity functions in C# (or Python or JS). They never write an orchestrator function again. Instead, a YAML file describes *what* the workflow does, and the framework drives the Durable Functions runtime underneath.

The orchestrator becomes a one-liner:

```csharp
public class OrderFulfillmentOrchestrator(IWorkflowDefinitionRegistry registry)
{
    [Function("OrderFulfillment")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowAsync(registry);
}
```

Everything else lives in YAML.

---

## Format Decision: YAML with Step Types

A directed graph is the conceptually correct model for workflows (they are DAGs), but graph DSLs require a visual editor to stay readable. YAML with a step-type system covers 90%+ of real-world patterns while remaining hand-authorable. The escape hatch for genuinely complex cases is writing a real orchestrator function — not everything has to be declarative.

The model: **steps run sequentially by default**. Explicit `type:` values handle parallel, fan-out, sub-orchestration, and external event waits.

---

## Supported Patterns

### 1. Activity call (default)

```yaml
- name: ValidateOrder
  activity: ValidateOrderActivity
  input: "{{input}}"
  output: validationResult
  retry:
    maxAttempts: 3
    firstRetryInterval: PT5S
```

### 2. Sub-orchestration (sequential wait)

```yaml
- name: RunSubWorkflow
  type: sub-orchestration
  workflow: OrderValidation
  input: "{{input}}"
  instanceId: "{{input.orderId}}"  # optional; omit to use context.NewGuid()
  output: validationResult
```

### 3. Fan-out: activity over items → Task.WhenAll

```yaml
- name: ReserveInventory
  type: foreach
  source: "{{input.lineItems}}"
  activity: ReserveItemActivity
  input:
    parent:
      orchestrationId: "{{orchestration.instanceId}}"
      correlationId: "{{input.correlationId}}"
    data: "{{$item}}"
  output: reservations
```

### 4. Fan-out: sub-orchestration over items → Task.WhenAll

```yaml
- name: FulfillLineItems
  type: foreach
  source: "{{input.lineItems}}"
  workflow: FulfillLineItem
  input:
    parent:
      orchestrationId: "{{orchestration.instanceId}}"
      correlationId: "{{input.correlationId}}"
    data: "{{$item}}"
  instanceId: "{{$item.lineItemId}}"  # optional; omit to use context.NewGuid()
  output: fulfillmentResults
```

### 5. Parallel block (fan-out/fan-in)

A parallel block launches all child steps concurrently from the same starting state and waits for all of them before continuing. It is a fan-in construct: child results are collected into a single aggregate object exposed to downstream steps.

```yaml
- name: Finalize
  type: parallel
  output: finalize
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
    ledger: "{{finalize.UpdateLedger}}"
```

**Parallel block semantics:**

- Each branch starts from a **snapshot** of the parent context at block-start. Branches cannot observe each other's outputs while running.
- The block's `output:` field names where the aggregate result object is stored in the parent context. If omitted, branch results are awaited but not exposed.
- The aggregate is an object keyed by **child step name**. Every named child step contributes an entry. Null entries are always included — they are never omitted.
- `output:` on a child step inside a parallel block is a **load-time error**. Branch results are always keyed by step name; there is no per-child output alias.
- Child step names must be unique within the block.
- If any branch fails, the parallel block fails.

**Branch result by step type** (what goes into the aggregate under the child's step name):

| Child step type | Branch result |
|---|---|
| `activity` | Return value of the activity call |
| `sub-orchestration` | Return value of the sub-orchestration |
| `foreach` | Array of all iteration results |
| `wait-for-event` | Event payload; `null` if `on-timeout: continue` fires |
| `poll` | Last activity result satisfying `until`; `null` if `on-timeout: continue` fires |
| `switch` | `null` — switch routes execution and has no return value |
| `parallel` (nested) | The nested block's aggregate object |
| Any step with `condition: false` | `null` — the step was skipped |

Nested path traversal works naturally. If `SendConfirmation` returns `{ "confirmationId": 123, "approvedBy": "James Scott" }`:

```yaml
input:
  id: "{{finalize.SendConfirmation.confirmationId}}"
  approver: "{{finalize.SendConfirmation.approvedBy}}"
```

### 6. External event wait (human approval / external agent)

```yaml
- name: WaitForApproval
  type: wait-for-event
  event: OrderApproved
  timeout: P7D
  on-timeout: fail        # fail (default) | continue
  output: approval
```

The instance ID is available as `{{orchestration.instanceId}}` — share it with external agents (via Service Bus, email, etc.) so they can call back via `RaiseEventAsync` or the task-completed endpoint.

### 7. Conditional branching

Simple condition on any step:

```yaml
- name: ChargeLateFee
  activity: ChargeLateFeeActivity
  condition: "{{approval.daysWaited > 3}}"
```

Switch/case for routing:

```yaml
- name: RouteByRegion
  type: switch
  on: "{{input.region}}"
  cases:
    EU:
      - activity: ApplyEUComplianceActivity
      - activity: RouteToEUFulfillmentActivity
    US:
      - activity: RouteToUSFulfillmentActivity
    default:
      - activity: RouteToGlobalFulfillmentActivity
```

### 8. Polling loop

Repeatedly calls an activity until a condition is met or a timeout expires. Implemented internally as a sub-orchestration that calls `ContinueAsNew` after each failed check to keep history manageable.

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
```

**Poll semantics:**

- The activity is called with the resolved `input` on every iteration.
- After each call, the result is bound to the step's `output` name and the `until` expression is evaluated against it.
- If `until` is true, the step completes and the result is stored in the parent context under `output`.
- If `until` is false and the wall-clock `timeout` has not elapsed, the engine sleeps for `delay` (ISO 8601 duration) then retries via `ContinueAsNew`.
- If `timeout` elapses: `on-timeout: fail` (default) throws and fails the orchestration; `on-timeout: continue` stores the last activity result (which may be `null`) and proceeds.
- `ContinueAsNew` resets the orchestration history on each iteration — required for long polling windows (e.g., `PT30D`) where unbounded history growth would otherwise occur.
- The `until` expression has full access to the expression language: comparisons, logical operators, null checks, nested property access.

### 9. Combinations

All of the above compose freely. A `parallel` block can contain `foreach` steps. A `foreach` can invoke a sub-orchestration that itself has a `wait-for-event`. A `poll` step can appear inside a `parallel` block. Any nesting depth.

---

## Expression Language

Expressions use `{{...}}` syntax throughout input mappings and conditions.

| Expression | Resolves to |
|---|---|
| `{{orchestration.instanceId}}` | Current orchestration instance ID |
| `{{orchestration.parentInstanceId}}` | Parent orchestration ID |
| `{{input}}` | Full workflow input object |
| `{{input.field}}` | Field from workflow input |
| `{{stepName}}` | Full output of a named step |
| `{{stepName.field}}` | Field from a step's output |
| `{{$item}}` | Current loop item (foreach only) |
| `{{$item.field}}` | Field from current loop item |
| `{{$index}}` | Loop index, 0-based (foreach only) |

**Evaluation rule**: If the entire YAML value is a single `{{...}}` expression, the result preserves the original type (object, array, number, bool). If the expression is embedded in a string (`"Order {{$item.id}} received"`), it is stringified and interpolated.

---

## Input Envelope Convention

When passing items into foreach loops (or any sub-orchestration), the recommended pattern is a parent/data envelope. This carries correlation metadata for logging without polluting the actual work item:

```yaml
input:
  parent:
    orchestrationId: "{{orchestration.instanceId}}"
    correlationId: "{{input.correlationId}}"
    workflowName: "OrderFulfillment"
  data: "{{$item}}"
```

The NuGet package ships a matching C# base type:

```csharp
public class WorkflowInput<TData>
{
    public WorkflowMetadata Parent { get; set; }
    public TData Data { get; set; }
}

public class WorkflowMetadata
{
    public string OrchestrationId { get; set; }
    public string? CorrelationId { get; set; }
    public string? WorkflowName { get; set; }
}
```

Activity signatures become clean and self-documenting:

```csharp
public class FulfillLineItemActivity
{
    [Function("FulfillLineItemActivity")]
    public async Task<FulfillmentResult> RunAsync(
        [ActivityTrigger] WorkflowInput<LineItem> input,
        FunctionContext executionContext)
    {
        var log = executionContext.GetLogger<FulfillLineItemActivity>();
        log.LogInformation("Fulfilling {ItemId} for orchestration {OrcId}",
            input.Data.Id, input.Parent.OrchestrationId);
    }
}
```

---

## Engine Architecture

```
WorkflowDefinition
  Loaded from YAML at host startup — never inside the orchestrator (replay-safety rule).
  Cached per workflow name. Represents the full step tree.

WorkflowRunner
  Extension method on TaskOrchestrationContext (isolated worker model).
  Walks steps sequentially; dispatches by StepType:
    Activity         → context.CallActivityAsync(name, resolvedInput)
    SubOrchestration → context.CallSubOrchestratorAsync(workflowName, resolvedInput, new SubOrchestrationOptions { InstanceId = id })
    Foreach          → items.Select(i => Dispatch(step, i)) → Task.WhenAll
    Parallel         → steps.Select(s => Dispatch(s)) → Task.WhenAll
    WaitForEvent     → context.WaitForExternalEvent<JsonElement>(name) raced against timer
    Switch           → evaluate expression → walk matching case steps
    Poll             → built-in DeclarativeWorkflowPoller sub-orchestration: call activity → evaluate until → ContinueAsNew with delay, or return

WorkflowExecutionContext
  Carries resolved step outputs by name.
  Exposes built-in variables (orchestration.instanceId, etc.).
  Passed through the entire step walk.

ExpressionEvaluator
  Resolves {{...}} expressions against WorkflowExecutionContext.
  Applies whole-value vs embedded-interpolation rule.
```

---

## What Developers Still Write

- **Activity functions** — all business logic lives here, in C#/Python/JS as normal
- **Trigger functions** — HTTP, Service Bus, Timer, etc. that start orchestrations (may also be generated by the framework eventually)
- **The orchestrator stub** — a one-liner that calls `context.RunWorkflowAsync(registry)`

They never write orchestrator logic again.

---

## Delivery

- NuGet package: `DeclarativeDurableFunctions` (name TBD)
- Targets .NET isolated worker model (the current/future standard)
- C# first; Python and JS are future consideration
- No dependency on a visual designer — YAML is the source of truth
