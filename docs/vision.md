# Vision: Declarative Durable Functions

CROW = Composable Runtime for Orchestrated Workflows

## The Problem

Azure Durable Functions are powerful — stateful, reliable, long-running workflows in a serverless environment. But the orchestrator function code is notoriously painful to write and read. The `await context.CallActivityAsync(...)` boilerplate, the replay constraints, the ceremony around fan-out/fan-in and external events — all of it turns developers off and keeps them from adopting Durable Functions at all.

## The Vision

A NuGet package that puts a declarative YAML layer on top of Durable Functions. Developers still write all their activity functions in C# (or Python or JS). They never write an orchestrator function again. Instead, a YAML file describes *what* the workflow does, and the framework drives the Durable Functions runtime underneath.

Everything lives in YAML.

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

### 7. Send-and-wait (fire-and-callback)

Calls an activity and waits for an external callback event concurrently. The event listener is registered **before** the activity is called — this is required to eliminate a race condition where a fast external process raises the callback before the orchestrator has registered interest in it. This race has been observed in production with Azure Service Bus: the activity sends a message, the downstream system processes it and calls `RaiseEventAsync`, and by the time the orchestrator resumes from the activity and reaches a `wait-for-event` step, the event has already been missed (or must be re-delivered). The sequential `activity` → `wait-for-event` pattern is therefore unsafe for this use case.

```yaml
- name: ProcessOrder
  type: trigger-and-wait
  activity: SendOrderToProcessorActivity
  input: "{{input.order}}"
  event: OrderProcessed
  timeout: PT60M
  on-timeout: fail        # fail (default) | continue
  output: processingResult
```

**Send-and-wait semantics:**

- The external event listener is registered first, then the activity is called. Both run concurrently.
- The step waits for the event and a timer to race. The activity is always awaited before the step completes (it is expected to be fast — a send, not the actual work).
- If the event fires: the timer is cancelled and the event payload is stored under `output`.
- If `timeout` elapses: behaves identically to `wait-for-event` — `on-timeout: fail` throws `WorkflowTimeoutException`; `on-timeout: continue` stores `null` and proceeds.
- If `timeout` is omitted: waits indefinitely for the event (activity is still awaited first).

### 8. Conditional branching

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

### 9. Polling loop

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

### 10. Combinations

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

## Workflow Versioning

### Why versioning is non-negotiable

Durable Functions uses event sourcing: every action an orchestrator takes is recorded as a history event in Azure Storage. When an orchestration resumes after a wait — or after a system restart — the runtime **replays** the orchestrator function from the beginning, using stored history to short-circuit already-completed steps without re-executing them. This means the YAML definition must produce exactly the same sequence of Durable calls on replay as it did on first run. If the YAML has changed between start and replay — steps added, removed, or reordered — the replay produces a different call sequence, causing non-determinism errors or silently wrong behavior.

The YAML file is effectively the orchestrator code. The same rules apply.

### File naming and the version field

Each YAML file carries an explicit version number:

```yaml
# OrderFulfillment-v2.yaml
workflow:
  name: Order Fulfillment
  version: 2
  steps:
    ...
```

File naming convention: `WorkflowName-vN.yaml`. The base name is derived from the file name by stripping the `-vN` suffix. The `version:` field inside the YAML is the source of truth for the integer; omitting it defaults to `1`. The `name:` field is a human-readable display name only and does not affect registry lookups.

### How version pinning works

When a new orchestration instance is scheduled (e.g., via `POST /api/workflows/OrderFulfillment`), the framework resolves the workflow name to its current latest versioned form — `"OrderFulfillment:v2"` — and stores that versioned name in the Durable instance's input envelope in Azure Storage. From that moment forward, every replay of that instance reads its versioned name from its own storage record and looks up the correct definition, regardless of how many newer versions have been deployed.

**The version is pinned at scheduling time, not at replay time.**

### Registry key format

Versioned names use the colon convention: `"WorkflowName:vN"` (e.g., `"OrderFulfillment:v2"`). When starting a workflow without a version specifier, the framework resolves to the latest registered version and pins it. All versions present on disk are registered simultaneously; the "latest" tracking is only used for new instance scheduling — never during replay.

### Sub-orchestration versioning

Sub-orchestrations get identical treatment. When a parent orchestration dispatches a sub-orchestration step (`workflow: FulfillLineItem`), the framework resolves `"FulfillLineItem"` to `"FulfillLineItem:v1"` (the current latest) at the moment `CallSubOrchestratorAsync` is called. Durable writes two things to Azure Storage at that moment:

1. A `SubOrchestrationStarted` event in the **parent's** history — recording that a sub-orchestration with a specific instance ID was scheduled
2. A new **sub-orchestration instance record** with its own input: `{ "__workflow": "FulfillLineItem:v1", ... }`

The sub-orchestration's versioned name is baked into its own input record. If the host restarts while the sub-orchestration is itself mid-flight — parked on its own `wait-for-event` — Durable resumes it as a fully independent instance, reads `"FulfillLineItem:v1"` from its own input, and replays against the v1 definition. The parent's version and the sub-orchestration's version are completely independent.

### Real-world scenario: parent at v1, sub-orchestration updated to v3

Consider a 10-step `OrderFulfillment:v1` that dispatches a `FulfillLineItem` sub-orchestration at step 3, then parks at a human-approval `wait-for-event` at step 8. While it's parked, `FulfillLineItem` is deployed to v2, then v3.

After a system restart:

- The parent's instance input still says `"__workflow": "OrderFulfillment:v1"` — replays against v1 ✅
- Step 3 (the sub-orchestration dispatch) was already recorded as completed in the parent's history. The replay short-circuits it and returns the stored result without touching the sub-orchestration's instance at all ✅
- Step 8 (the wait-for-event) is still pending — re-created correctly ✅
- `FulfillLineItem-v1.yaml` is still on disk and still registered, so if the sub-orchestration were itself still mid-flight, it would also replay correctly against v1 ✅

If instead the sub-orchestration was also still waiting (parked on its own internal wait step):

- Durable holds two separate pending instance records in storage
- Each reads its own `__workflow` value from its own input envelope
- Each replays independently against its own pinned version
- The parent never needs to know what version the sub-orchestration is running — it only sees the completed result

### The append-only constraint

YAML files are **effectively append-only** while instances referencing them are alive. You can deploy `FulfillLineItem-v3.yaml`, but you cannot remove `FulfillLineItem-v1.yaml` until every instance pinned to v1 has completed or been terminated. All version files are loaded into the registry at host startup; a missing version file for a live instance will cause that instance's replay to fail.

For the file-based registry this is an operational discipline constraint — enforced by awareness rather than tooling. For an Azure Blob Storage registry, it is automatic: blobs persist until explicitly deleted, so old versions remain retrievable indefinitely even after newer versions are deployed.

### The narrow race condition

There is one thin window: if the host crashes after an orchestrator decides to schedule a sub-orchestration but before Durable finishes writing the `SubOrchestrationStarted` history event to storage, that scheduling step will replay on the next host start. On replay, the framework re-resolves the workflow name from the registry and may now get a newer version than on the original run. This is the same non-determinism window that exists in any Durable Functions orchestrator when code changes while events are in flight — not specific to this framework. The only ironclad protection is draining in-flight instances before deploying new versions.

---

## What Developers Still Write

- **Activity functions** — all business logic lives here, in C#/Python/JS as normal
- **Trigger functions** — HTTP, Service Bus, Timer, etc. that start orchestrations (may also be generated by the framework eventually)

They never write orchestrator logic again.

---

## Delivery

- NuGet package: `DeclarativeDurableFunctions` (name TBD)
- Targets .NET isolated worker model (the current/future standard)
- C# first; Python and JS are future consideration
- No dependency on a visual designer — YAML is the source of truth
