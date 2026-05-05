# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

A NuGet package (`DeclarativeDurableFunctions`) that adds a declarative YAML layer on top of Azure Durable Functions. Developers write activity functions in C# as normal and describe the orchestration in YAML — they never write an orchestrator function. The framework drives the Durable Functions runtime underneath.

See [`docs/vision.md`](docs/vision.md) for the full design: YAML schema, expression language, engine architecture, and the input envelope convention.

## Target platform

- Azure Functions — .NET **isolated worker model** only (not in-process)
- C# first; Python/JS are future considerations
- NuGet package delivery

## Build and test commands

> The project is in early design phase. Update this section as the solution structure is established.

Standard .NET commands will apply once the solution exists:

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~SomeTestClass"
dotnet pack
```

## Architecture

The engine has four main parts. Keep them separated:

**`WorkflowDefinition` / `StepDefinition`** — the parsed, in-memory representation of a YAML workflow. Loaded once at host startup and cached. Must never be loaded or fetched inside an orchestrator function (Durable Functions replay constraint: orchestrators must be deterministic and I/O-free).

**`WorkflowRunner`** — an extension method on `TaskOrchestrationContext` (isolated worker model) invoked as `context.RunWorkflowAsync(registry)`. Walks the step tree sequentially and dispatches by `StepType`:

| StepType | Durable Functions call |
|---|---|
| `Activity` | `context.CallActivityAsync(name, resolvedInput)` |
| `SubOrchestration` | `context.CallSubOrchestratorAsync(workflowName, resolvedInput, new SubOrchestrationOptions { InstanceId = id })` |
| `Foreach` | `items.Select(i => Dispatch(step, i))` → `Task.WhenAll` |
| `Parallel` | `steps.Select(s => Dispatch(s))` → `Task.WhenAll` |
| `WaitForEvent` | `context.WaitForExternalEvent<JsonElement>(name)` raced against a timer |
| `Switch` | evaluate expression → walk matching case steps |
| `Poll` | built-in `DeclarativeWorkflowPoller` sub-orchestration: call activity → evaluate `until` → `ContinueAsNew` with delay, or return |

**`WorkflowExecutionContext`** — carries resolved step outputs by name and exposes built-in variables (`orchestration.instanceId`, `orchestration.parentInstanceId`). Flows through the entire step walk.

**`ExpressionEvaluator`** — resolves `{{...}}` expressions against `WorkflowExecutionContext`. One critical rule: if the entire YAML value is a single `{{...}}` expression, the result preserves the original type (object, array, number). If the expression is embedded in a string (`"Order {{$item.id}} received"`), it stringifies and interpolates.

## Framework-shipped functions

The library ships four Azure Functions in `DeclarativeDurableFunctions.Functions`. The Functions runtime auto-discovers them from the library assembly — consumers get them for free with no registration required:

| Class | Function name | Purpose |
|---|---|---|
| `GenericOrchestrator` | `GenericOrchestrator` | Top-level orchestrator; routes to correct workflow by name |
| `GenericSubOrchestrator` | `GenericSubOrchestration` | Sub-orchestrator used by `sub-orchestration` and `foreach` steps |
| `GenericHttpTrigger` | `StartWorkflow` | `POST /api/workflows/{workflowName}` — starts any workflow |
| `GenericEventTrigger` | `EventTrigger` | `POST /api/events/{instanceId}/{eventName}` — raises an external event |
| `DeclarativePollerOrchestrator` | `DeclarativeWorkflowPoller` | Built-in sub-orchestrator for `type: poll` steps |

Do not move these back to the test app. Adding this library to a Functions app is opting into this as a framework-level replacement for manually written orchestrators and triggers.

## Key design decisions (do not relitigate without good reason)

- **YAML over directed graphs.** Directed graphs require a visual editor to stay readable. YAML with step types covers 90%+ of real-world patterns hand-authorably. The escape hatch for genuinely complex cases is writing a real orchestrator function.
- **Sequential by default.** Steps execute in order unless wrapped in `type: parallel` or `type: foreach`.
- **`WorkflowDefinition` is loaded at host startup, not inside the orchestrator.** This is non-negotiable — loading from Blob/network inside an orchestrator violates Durable Functions' determinism/replay requirements.
- **`$item` loop variable convention.** In `foreach` steps, `{{$item}}` is the current item and `{{$index}}` is the 0-based index.
- **Sub-orchestration instance IDs.** The optional `instanceId` field on `sub-orchestration` and `foreach` steps takes an expression (e.g. `{{$item.orderId}}`). When omitted, `context.NewGuid()` is used — never `Guid.NewGuid()`, which is non-deterministic across replays. Full ID format: `{parentInstanceId}:{stepName}:{suffix}`.
- **Input envelope convention.** The recommended pattern for passing items into sub-orchestrations is a `parent` / `data` envelope. The package ships `WorkflowInput<TData>` and `WorkflowMetadata` base types to match. Don't break this contract.
- **`switch` doubles as if-else.** A boolean expression in `on:` evaluates to the lowercase string `"true"` or `"false"`. Case keys must match that casing. `ExpressionEvaluator.Stringify` is the single source of truth for value-to-string coercion — do not duplicate it.
- **`poll` on-timeout: continue returns the last activity result**, not null. This differs from `wait-for-event` on-timeout: continue, which stores null. The distinction is intentional: a timed-out poll still has a real (if unsatisfying) result; a timed-out event wait has nothing.
- **No helper classes.** When shared logic is needed across engine types, extract it to the type that owns the responsibility (e.g. `ExpressionEvaluator.Stringify`) rather than creating a `*Helpers` or `*Utils` class.

## `WorkflowInput<TData>` — the envelope type

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

## External event pattern

The `wait-for-event` step type maps to `context.WaitForExternalEvent<JsonElement>()` raced against a Durable timer. `{{orchestration.instanceId}}` is available in any input expression so workflows can pass their own ID to external agents (Service Bus, HTTP callbacks, etc.) that will later call `RaiseEventAsync` or the task-completed endpoint to resume the orchestration.
