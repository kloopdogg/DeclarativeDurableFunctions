# Implementation Plan

This document is the execution blueprint for building `DeclarativeDurableFunctions`. It specifies phases, agent assignments, model choices, parallelism opportunities, and review gates (human + Copilot) for each checkpoint.

---

## Critical path

```
Phase 0 (Scaffold)
  → Phase 1 (Models) — parallel agents
      → Phase 2 (Infrastructure) — parallel agents
          → Phase 3 (WorkflowRunner) — single agent, critical path
              → Phase 4 (Tests) — parallel agents
                  → Phase 5 (Integration + Polish)
```

Wall clock estimate with parallelism: ~2.5 hours. Sequential equivalent: ~7 hours.

---

## Phase 0 — Scaffold

**What:** Create the solution structure. No logic — just the skeleton everything else builds on.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- `DeclarativeDurableFunctions/DeclarativeDurableFunctions.csproj` — class library targeting `net8.0`, isolated worker SDK + YamlDotNet refs
- `DeclarativeDurableFunctions.Tests/DeclarativeDurableFunctions.Tests.csproj` — xUnit test project
- `DeclarativeDurableFunctions.Sample/DeclarativeDurableFunctions.Sample.csproj` — Azure Functions isolated worker app
- `DeclarativeDurableFunctions.sln` — solution file

---

### Gate 1 — After Phase 0

#### Human review
Check that:
- All three projects target the right frameworks (`net8.0` for the library and sample, `net8.0` for tests)
- The class library references `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` (isolated worker, not the in-process package)
- YamlDotNet is referenced in the class library
- xUnit + Moq (or NSubstitute) are referenced in the test project
- The sample project references the class library project (not a NuGet path yet)

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the scaffolded solution structure for `DeclarativeDurableFunctions`. The project adds a declarative YAML layer on top of Azure Durable Functions (isolated worker model). You have already read `CLAUDE.md` and `README.md` for full context.
>
> Review the following files and answer each question:
>
> - `DeclarativeDurableFunctions/DeclarativeDurableFunctions.csproj`
> - `DeclarativeDurableFunctions.Tests/DeclarativeDurableFunctions.Tests.csproj`
> - `DeclarativeDurableFunctions.Sample/DeclarativeDurableFunctions.Sample.csproj`
> - `DeclarativeDurableFunctions.sln`
>
> **Questions:**
> 1. Does the class library reference the **isolated worker** Durable Functions package (`Microsoft.Azure.Functions.Worker.Extensions.DurableTask`), not the in-process package (`Microsoft.Azure.WebJobs.Extensions.DurableTask`)? This is non-negotiable — the in-process model is deprecated.
> 2. Is YamlDotNet present in the class library? Is the version recent enough to support deserializing to POCOs?
> 3. Does the test project have xUnit and a mocking library (Moq or NSubstitute)?
> 4. Are there any unnecessary dependencies that could introduce conflicts or bloat?
> 5. Is there anything structurally wrong with the solution layout that will cause friction later?
>
> Return a verdict: **Pass** or **Needs changes**, with a bullet list of any specific issues.

---

## Phase 1 — Core Models

**What:** The in-memory representation of a parsed YAML workflow. These are the contracts every subsequent component builds against — get them right before anything else runs.

**Parallelism:** 2 agents running simultaneously.

### Agent 1A — Workflow model
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions/Models/WorkflowDefinition.cs`, `StepDefinition.cs`, `StepType.cs`

Covers:
- `WorkflowDefinition` — name + ordered list of `StepDefinition`
- `StepDefinition` — name, type, activity name, workflow name (for sub-orchestration/foreach), source expression, input (object), output name, condition expression, instanceId expression, retry config, timeout, on-timeout, nested steps (for parallel/switch), switch cases
- `StepType` enum — `Activity`, `SubOrchestration`, `Foreach`, `Parallel`, `WaitForEvent`, `Switch`
- `RetryPolicy` — maxAttempts, firstRetryInterval (ISO 8601 duration string)

### Agent 1B — Envelope types
**Model:** Haiku  
**Output:** `DeclarativeDurableFunctions/Models/WorkflowInput.cs`, `WorkflowMetadata.cs`

Covers exactly the types from the spec:
```csharp
public class WorkflowInput<TData> { WorkflowMetadata Parent; TData Data; }
public class WorkflowMetadata { string OrchestrationId; string? CorrelationId; string? WorkflowName; }
```

---

### Gate 2 — After Phase 1

**This is the most important gate.** Every subsequent component builds against these shapes. Changes after this gate ripple into three parallel agents.

#### Human review
Check that:
- `StepType` covers all six step types from the spec (Activity, SubOrchestration, Foreach, Parallel, WaitForEvent, Switch)
- `StepDefinition` has fields for both `activity` (activity name) and `workflow` (workflow name for sub-orchestration/foreach) — they serve different step types
- Nested steps (`steps:` list on parallel, cases on switch) are modeled correctly
- `condition` is a plain string (expression to be evaluated later, not evaluated here)
- `instanceId` is a plain string (same)
- `WorkflowInput<TData>` and `WorkflowMetadata` match the spec exactly — don't gold-plate

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the core model classes for `DeclarativeDurableFunctions`. These are the in-memory representation of a parsed YAML workflow definition. You have already read `CLAUDE.md` and `README.md`.
>
> Review the following files:
>
> - `DeclarativeDurableFunctions/Models/WorkflowDefinition.cs`
> - `DeclarativeDurableFunctions/Models/StepDefinition.cs` (and `StepType.cs` if separate)
> - `DeclarativeDurableFunctions/Models/WorkflowInput.cs`
> - `DeclarativeDurableFunctions/Models/WorkflowMetadata.cs`
>
> **Questions:**
> 1. Does `StepType` include all six step types described in `docs/vision.md`: Activity, SubOrchestration, Foreach, Parallel, WaitForEvent, Switch?
> 2. Does `StepDefinition` have separate fields for `activity` (activity function name) and `workflow` (workflow name for sub-orchestration and foreach)? These are distinct concepts — conflating them would be a bug.
> 3. Does `StepDefinition` model nested steps correctly? `type: parallel` and switch cases both contain child step lists.
> 4. Are `condition` and `instanceId` stored as raw strings (unevaluated expressions), not evaluated at parse time?
> 5. Does `StepDefinition.Input` use a type flexible enough to hold arbitrary YAML structures (object, array, string, number)? A plain `string` type would be too narrow.
> 6. Does `WorkflowInput<TData>` and `WorkflowMetadata` exactly match the envelope contract in `CLAUDE.md`?
> 7. Are there any fields that appear on `StepDefinition` that conflict with the replay-safety rules described in `CLAUDE.md` (e.g., anything that would cause non-deterministic behavior if evaluated during model construction)?
>
> Return a verdict: **Pass** or **Needs changes**, with a bullet list of specific issues.

---

## Phase 2 — Core Infrastructure

**What:** The three engine components that `WorkflowRunner` depends on. All three depend only on Phase 1 models and are fully independent of each other.

**Parallelism:** 3 agents running simultaneously.

### Agent 2A — ExpressionEvaluator
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions/Engine/ExpressionEvaluator.cs`

Key rule to implement correctly: if the **entire** YAML value is a single `{{...}}` expression, the result preserves the original type (object, array, number, bool). If the expression is **embedded** in a string (`"Order {{$item.id}} received"`), it stringifies and interpolates.

Resolves all expression tokens defined in the spec: `orchestration.instanceId`, `orchestration.parentInstanceId`, `input`, `input.field`, `stepName`, `stepName.field`, `$item`, `$item.field`, `$index`.

### Agent 2B — WorkflowExecutionContext
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions/Engine/WorkflowExecutionContext.cs`

A context object that flows through the entire step walk:
- Dictionary of step outputs keyed by step name
- Built-in variables: `orchestration.instanceId`, `orchestration.parentInstanceId`
- Scoped fork for foreach iterations (carries `$item` and `$index` without polluting parent context)

### Agent 2C — YAML Loader / Registry
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions/Engine/IWorkflowDefinitionRegistry.cs`, `WorkflowDefinitionRegistry.cs`

- `IWorkflowDefinitionRegistry` — `GetDefinition(string workflowName): WorkflowDefinition`
- `WorkflowDefinitionRegistry` — loads from a directory of `.yaml` files at startup, caches by workflow name
- Must be registered as a singleton (enforced by DI lifetime; the registry itself is stateless after load)
- Must never be called inside an orchestrator (this is a consumer responsibility, but a `[Obsolete]` warning or XML doc note is appropriate)

---

## Phase 3 — WorkflowRunner

**What:** The heart of the engine. Extension method on `TaskOrchestrationContext` that walks the step tree and dispatches each step type to the correct Durable Functions runtime call.

**Agent:** 1 agent — no parallelism. Recursive dispatch structure requires coherent authorship.  
**Model:** **Opus** — the only place in the plan where Opus is warranted. Replay-safety constraints, correct async patterns, and recursive step dispatch all in one piece. Mistakes here cascade into every test.

**Output:** `DeclarativeDurableFunctions/Engine/WorkflowRunner.cs`

Dispatch table (from `CLAUDE.md`):

| StepType | Durable Functions call |
|---|---|
| Activity | `context.CallActivityAsync(name, resolvedInput)` |
| SubOrchestration | `context.CallSubOrchestratorAsync(workflowName, resolvedInput, new SubOrchestrationOptions { InstanceId = id })` |
| Foreach | `items.Select(i => Dispatch(step, i))` → `Task.WhenAll` |
| Parallel | `steps.Select(s => Dispatch(s))` → `Task.WhenAll` |
| WaitForEvent | `context.WaitForExternalEvent<JsonElement>(name)` raced against a timer |
| Switch | evaluate expression → walk matching case steps |

Critical correctness requirements:
- Instance ID generation **must** use `context.NewGuid()`, never `Guid.NewGuid()` — the latter is non-deterministic across replays
- Instance ID format: `{parentInstanceId}:{stepName}:{suffix}`
- `condition` fields must be evaluated before executing the step; skip step if false
- `foreach` must fork a scoped `WorkflowExecutionContext` per iteration carrying `$item` and `$index`
- `wait-for-event` timeout uses ISO 8601 duration parsed into a `TimeSpan`; `on-timeout: continue` vs `fail` controls whether timeout throws or continues

---

### Gate 3 — After Phase 3

**Do not write tests until this gate passes.** Tests written against a broken runner cement wrong behavior.

#### Human review
Check that:
- `context.NewGuid()` is used everywhere an instance ID is generated — grep for `Guid.NewGuid` and it should return zero results in `WorkflowRunner.cs`
- The `foreach` dispatch creates a scoped context per iteration (not a shared one)
- `Task.WhenAll` is used correctly for both `foreach` and `parallel` — no accidental sequential execution
- `wait-for-event` uses `Task.WhenAny` to race the event against the timer, not `await` on both
- The `switch` default case is handled (no case match → run default steps or continue)
- Retry policy on Activity steps wires into `CallActivityOptions` correctly

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the `WorkflowRunner` implementation for `DeclarativeDurableFunctions`. This is the core engine — an extension method on `TaskOrchestrationContext` (isolated worker model) that walks a parsed YAML workflow step tree and dispatches each step to the Durable Functions runtime. You have already read `CLAUDE.md` and `README.md`.
>
> Review the following files:
>
> - `DeclarativeDurableFunctions/Engine/WorkflowRunner.cs`
> - `DeclarativeDurableFunctions/Engine/ExpressionEvaluator.cs`
> - `DeclarativeDurableFunctions/Engine/WorkflowExecutionContext.cs`
>
> **Replay safety (highest priority):**
> 1. Is `context.NewGuid()` used for all instance ID generation? Search the file for `Guid.NewGuid` — if it appears anywhere in `WorkflowRunner.cs`, that is a critical bug. Durable Functions replays the orchestrator from scratch on each checkpoint; `Guid.NewGuid()` produces a different value on each replay, breaking determinism.
> 2. Are there any calls to `DateTime.Now`, `DateTime.UtcNow`, `Random`, or other non-deterministic APIs inside `WorkflowRunner.cs`? These are all replay-safety violations.
> 3. Are there any file I/O, network, or logging calls that would be non-deterministic across replays? (Structured logging via `ILogger` from `TaskOrchestrationContext` is fine; raw `Console.Write` is not.)
>
> **Correctness:**
> 4. For `foreach` steps: does the runner create a **scoped** `WorkflowExecutionContext` per iteration that carries `$item` and `$index` without mutating the parent context?
> 5. For `foreach` and `parallel` steps: does the runner use `Task.WhenAll` to execute child steps concurrently, not a sequential loop?
> 6. For `wait-for-event` steps: does the runner use `Task.WhenAny` to race the external event against a Durable timer? A plain `await context.WaitForExternalEvent(...)` with no timeout race is incorrect.
> 7. For `wait-for-event` with `on-timeout: continue`: does the runner continue execution rather than throw when the timer fires first?
> 8. For `switch` steps: is the default case handled when no case matches?
> 9. Does the runner evaluate `condition` expressions before executing each step, and skip the step when the condition is false?
> 10. For `Activity` steps with a retry policy: does the retry config wire into `CallActivityOptions` correctly?
>
> **Sub-orchestration instance IDs:**
> 11. Is the instance ID format `{parentInstanceId}:{stepName}:{suffix}` as specified?
> 12. When `instanceId` is omitted from a step, does the runner fall back to `context.NewGuid()` (not `Guid.NewGuid()`)?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of specific issues matching the question numbers above.

---

## Phase 4 — Tests

**What:** Unit test coverage for the three engine components and the WorkflowRunner. All four agents start simultaneously after Gate 3 passes.

**Parallelism:** 3 agents running simultaneously.

### Agent 4A — ExpressionEvaluator tests
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions.Tests/ExpressionEvaluatorTests.cs`

Cases to cover: whole-value single expression preserves type (object, array, number, bool), embedded interpolation stringifies, `$item` resolution, `$item.field` path traversal, `$index`, `orchestration.instanceId`, `orchestration.parentInstanceId`, multi-level `stepName.field.nested`, missing step output throws, missing field returns null or throws.

### Agent 4B — WorkflowRunner tests
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions.Tests/WorkflowRunnerTests.cs`

Uses a mock `TaskOrchestrationContext`. One test per step type verifying the correct Durable Functions call is made with correctly resolved input. Include: activity with retry, foreach fan-out (verify `Task.WhenAll`), wait-for-event timeout fires (verify continue vs fail), switch routing to correct case, condition false skips step.

### Agent 4C — YAML loader tests
**Model:** Sonnet  
**Output:** `DeclarativeDurableFunctions.Tests/WorkflowDefinitionRegistryTests.cs`

Round-trip tests: load the sample YAML files from `docs/vision.md` (inline as strings), verify `WorkflowDefinition` fields are populated correctly. Edge cases: missing required fields, unknown step type, invalid ISO 8601 duration.

---

### Gate 4 — After Phase 4

#### Human review
Check that:
- WorkflowRunner tests mock `TaskOrchestrationContext`, not the real Durable runtime
- The replay-safety bugs tested in Gate 3 have corresponding regression tests (instance ID generation, non-deterministic APIs)
- Expression evaluator tests cover the whole-value vs embedded interpolation distinction — this is the most common source of subtle bugs
- Test names describe behavior, not implementation

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the test suite for `DeclarativeDurableFunctions`. You have already read `CLAUDE.md` and `README.md`. The engine has three components: `ExpressionEvaluator`, `WorkflowExecutionContext`, and `WorkflowRunner`.
>
> Review the following files:
>
> - `DeclarativeDurableFunctions.Tests/ExpressionEvaluatorTests.cs`
> - `DeclarativeDurableFunctions.Tests/WorkflowRunnerTests.cs`
> - `DeclarativeDurableFunctions.Tests/WorkflowDefinitionRegistryTests.cs`
>
> **Coverage:**
> 1. Do the `ExpressionEvaluator` tests cover the **whole-value single expression preserves type** rule? This is the most subtle behavioral rule in the spec — a `{{stepOutput}}` that resolves to an array should remain an array, not be stringified.
> 2. Do the `ExpressionEvaluator` tests cover embedded interpolation (`"Order {{$item.id}} received"` → string)?
> 3. Do the `WorkflowRunner` tests cover all six step types: Activity, SubOrchestration, Foreach, Parallel, WaitForEvent, Switch?
> 4. Is there a test that verifies `wait-for-event` with `on-timeout: continue` does not throw?
> 5. Is there a test that verifies `condition: false` causes a step to be skipped?
> 6. Is there a test that verifies the correct Durable Functions call is made for sub-orchestration, including the instance ID format?
>
> **Replay safety regression tests:**
> 7. Is there a test (or at minimum a comment) confirming that `WorkflowRunner` uses `context.NewGuid()` and not `Guid.NewGuid()`? This was flagged as a critical concern in the implementation review.
>
> **Test quality:**
> 8. Are `TaskOrchestrationContext` calls verified via mocks (not the real Durable runtime)?
> 9. Do test names describe expected behavior rather than implementation details?
> 10. Are there any tests that look correct but are actually vacuous — e.g., asserting on a mock setup value rather than on a real call being made?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues.

---

## Phase 5 — Integration and Polish

**What:** Wire everything together in the sample app, add NuGet packaging config, and do a final README pass.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- Sample app: an order fulfillment Azure Function using `context.RunWorkflowAsync(registry)` with a YAML workflow file that exercises at least: activity, foreach (sub-orchestration), wait-for-event, parallel block
- `DeclarativeDurableFunctions.csproj` — `<PackageId>`, `<Version>`, `<Description>`, `<PackageLicense>` metadata
- `README.md` — status updated from "Early design phase" to "Alpha"; add install + quickstart section

No approval gate — if Gates 1–4 passed, this is a polish pass.

---

## Model assignment summary

| Phase | Agent | Model | Reason |
|---|---|---|---|
| 0 — Scaffold | 1 | Sonnet | Mechanical but wrong references are annoying to unwind |
| 1A — Workflow models | 1 | Sonnet | Step tree modeling has nuance |
| 1B — Envelope types | 1 | **Haiku** | Two data classes, three fields each |
| 2A — ExpressionEvaluator | 1 | Sonnet | Non-trivial but well-specified |
| 2B — ExecutionContext | 1 | Sonnet | Straightforward with scoping nuance |
| 2C — YAML Loader | 1 | Sonnet | Mechanical YamlDotNet wiring |
| 3 — WorkflowRunner | 1 | **Opus** | Recursive dispatch + replay safety + async patterns; bugs cascade |
| 4A — Evaluator tests | 1 | Sonnet | Mechanical enumeration of spec cases |
| 4B — Runner tests | 1 | Sonnet | Standard mock-based pattern |
| 4C — Loader tests | 1 | Sonnet | Round-trip + edge cases |
| 5 — Integration + polish | 1 | Sonnet | Spec-guided, creative |
