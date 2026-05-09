# Versioning Implementation Plan

This document is the execution blueprint for adding workflow versioning to `DeclarativeDurableFunctions`. It specifies phases, agent assignments, model choices, parallelism opportunities, and review gates (human + Copilot) for each checkpoint.

Read `docs/versioning-spec.md` fully before starting any phase. All design decisions are resolved there — do not relitigate them.

---

## Critical path

```
Phase 1 (Models)
  → Phase 2A (Loader) || Phase 2B (Registry)  [parallel]
      → Phase 3 (Integration Wiring)
          → Phase 4A (Registry tests) || Phase 4B (Runner tests)  [parallel]
              → Phase 5 (Spec doc updates)
```

Wall clock estimate with parallelism: ~1.5 hours. Sequential equivalent: ~3.5 hours.

---

## Phase 1 — Models

**What:** Add `Version`, `VersionedName`, and `WorkflowVersion` to the in-memory model types. Everything else builds on these — get them right first.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Models/WorkflowDefinition.cs` — add `Version` (int, default 1), `VersionedName` (computed property)
- `src/DeclarativeDurableFunctions/Models/StepDefinition.cs` — add `WorkflowVersion` (string?)

Exact shapes are specified in §4.1 and §4.2 of `docs/versioning-spec.md`.

---

### Gate 1 — After Phase 1

#### Human review
Check that:
- `WorkflowDefinition.Version` is `int` with default `1` (not `int?`, not `string`)
- `WorkflowDefinition.VersionedName` is a computed property (`=> $"{Name}:{Version}"`), not a stored field
- `WorkflowDefinition.Name` and `DisplayName` are unchanged
- `StepDefinition.WorkflowVersion` is `string?` (not `int?`) — stored as string for forward compatibility with opaque version strings
- `StepDefinition.WorkflowVersion` is placed in the `// Loop` region, after `LoopWorkflowName`
- No serialization attributes or JSON metadata added to either type — these are internal models

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the model changes for the versioning feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/versioning-spec.md`.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions/Models/WorkflowDefinition.cs`
> - `src/DeclarativeDurableFunctions/Models/StepDefinition.cs`
>
> **Questions:**
> 1. Is `WorkflowDefinition.Version` typed as `int` (not `int?` or `string`), with a default value of `1`?
> 2. Is `WorkflowDefinition.VersionedName` a computed property (`=> $"{Name}:{Version}"`), not a stored field? A stored field would be wrong because it could get out of sync.
> 3. Are the existing `Name` and `DisplayName` fields unchanged?
> 4. Is `StepDefinition.WorkflowVersion` typed as `string?` (not `int?`)? The spec requires string for forward compatibility with opaque version identifiers.
> 5. Is there any attempt to validate `WorkflowVersion` on the model type? Validation belongs in the loader and runner, not on the model.
> 6. Are any unnecessary fields or properties introduced beyond what the spec requires?
>
> Return a verdict: **Pass** or **Needs changes**, with a bullet list of specific issues.

---

## Phase 2 — Loader and Registry

**What:** Parse `version:` from YAML, compute versioned registry keys, wire up `ResolveVersionedName`. These two agents are fully independent of each other — run them in parallel.

**Parallelism:** 2 agents running simultaneously.

### Agent 2A — WorkflowDefinitionLoader
**Model:** Sonnet  
**Output:** `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionLoader.cs`

Implement exactly the changes in §5.1 of `docs/versioning-spec.md`:
- Add `StripVersionSuffix(string fileName)` using `Regex.Replace(fileName, @"-v\d+$", "")`
- Change `LoadAll` return type to `(IReadOnlyDictionary<string, WorkflowDefinition> Definitions, IReadOnlyDictionary<string, int> LatestVersions)`
- In `LoadAll` body: accumulate `latestVersions` dict; skip `__`-prefixed workflow names when building it
- In `LoadFromYamlCore`: parse `version:` field (default 1, throw `WorkflowDefinitionException` if `< 1`), construct `versionedName`, pass it as `workflowContext` to `ParseSteps`
- In `LoadFromYamlAll`: change the final registration line to use `def.VersionedName` as the accumulator key
- In `ParseStep`: read `version:` from the step dict and populate `WorkflowVersion`

### Agent 2B — Registry
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Engine/IWorkflowDefinitionRegistry.cs`
- `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionRegistry.cs`

Implement exactly the changes in §4.3, §4.4, and §5.2 of `docs/versioning-spec.md`:
- Add `ResolveVersionedName(string workflowName)` to the public `IWorkflowDefinitionRegistry` interface
- Replace the existing primary constructor with the new two-parameter constructor (`definitions`, `latestVersions`)
- Implement `ResolveVersionedName` with the passthrough rule: `Contains(':')` → return unchanged; otherwise look up `_latestVersions` and return `"{name}:{latest}"`
- Update `Get` to call `ResolveVersionedName` as the first step
- Update `TryGet` to apply the same passthrough/lookup logic inline
- `WorkflowNames` returns all versioned keys from `_definitions`

---

### Gate 2 — After Phase 2

**This is the most important gate.** The loader and registry are the single source of truth for versioned names — any bugs here cascade into every runtime dispatch.

#### Human review
Check that:
- Loader `StripVersionSuffix` handles both versioned files (`OrderFulfillment-v2.yaml` → `OrderFulfillment`) and unversioned files (`OrderFulfillment.yaml` → `OrderFulfillment`)
- `LoadFromYamlAll` accumulator key is `def.VersionedName` (e.g., `"OrderFulfillment:2"`), not the bare name
- `LoadAll` skips `__`-prefixed workflow names when building `latestVersions`; loop inner workflows must not pollute the public name table
- `ParseStep` reads `version:` and sets `WorkflowVersion` with no parse-time validation
- `ResolveVersionedName` passthrough triggers on `Contains(':')`, covering both `"Name:N"` and `"__loop__Name:N__Step"` forms
- `ResolveVersionedName` throws `WorkflowDefinitionException` for unknown unversioned names — not `KeyNotFoundException`
- `Get` calls `ResolveVersionedName` first; the internal key lookup always uses the versioned name
- Registry constructor stores both dicts as private readonly fields

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the loader and registry changes for the versioning feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/versioning-spec.md`.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionLoader.cs`
> - `src/DeclarativeDurableFunctions/Engine/IWorkflowDefinitionRegistry.cs`
> - `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionRegistry.cs`
>
> **Loader questions:**
> 1. Does `StripVersionSuffix` use a regex that strips only a trailing `-v` followed by digits — not any substring, and not a leading prefix? `"MyWorkflow-v2"` → `"MyWorkflow"`, `"MyWorkflow"` → `"MyWorkflow"` (unchanged).
> 2. Is `LoadAll`'s return type the two-element tuple `(IReadOnlyDictionary<string, WorkflowDefinition> Definitions, IReadOnlyDictionary<string, int> LatestVersions)`?
> 3. When building `latestVersions`, does `LoadAll` skip workflow definitions whose `Name` starts with `"__"`? These are internal loop inner workflows and must not appear in the public name table.
> 4. Does `LoadFromYamlCore` parse `workflow.version:` and throw `WorkflowDefinitionException` if the value is less than 1?
> 5. Does `LoadFromYamlCore` pass the versioned name (not the bare name) as `workflowContext` to `ParseSteps`? This is what causes loop inner workflow keys to embed the parent's versioned name.
> 6. Does `LoadFromYamlAll` register the definition under `def.VersionedName` (e.g., `"OrderFulfillment:2"`), not under the bare name?
> 7. Does `ParseStep` read `version:` from step-level YAML and populate `StepDefinition.WorkflowVersion` as a string (not parsed to int)?
>
> **Registry questions:**
> 8. Does `ResolveVersionedName` return the input unchanged when `workflowName.Contains(':')` — covering both `"Name:N"` and `"__loop__Name:N__Step"` forms?
> 9. Does `ResolveVersionedName` throw `WorkflowDefinitionException` (not `KeyNotFoundException` or `InvalidOperationException`) when the unversioned name is not in `_latestVersions`?
> 10. Does `Get` call `ResolveVersionedName` as its first step, so that both versioned and unversioned inputs work correctly?
> 11. Does `TryGet` apply the same passthrough/lookup logic without calling `Get` (to avoid double exception risk on missing names)?
> 12. Does `WorkflowNames` return the keys from `_definitions`, which should be fully versioned names including `__loop__` inner names?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Phase 3 — Integration Wiring

**What:** Wire the versioned name through the trigger, runner, and extension. Four files, one coherent agent — they share the same data flow and must be consistent.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Extensions/ServiceCollectionExtensions.cs` — destructure `LoadAll` tuple, pass both dicts to registry constructor (§5.6)
- `src/DeclarativeDurableFunctions/Functions/GenericHttpTrigger.cs` — inject `IWorkflowDefinitionRegistry`, remove `static` from `StartAsync`, resolve versioned name before scheduling (§5.3)
- `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs` — add `registry` parameter to `RunAsync`, resolve versioned name in `RunSubOrchestration` and `DispatchForeachSubOrch` (§5.4)
- `src/DeclarativeDurableFunctions/Extensions/DynamicOrchestrationContextExtensions.cs` — pass `internalRegistry` as 4th argument to `DynamicWorkflowRunner.RunAsync` (§5.5)

---

### Gate 3 — After Phase 3

**Do not write tests until this gate passes.** Tests against a broken wiring layer cement wrong behavior.

#### Human review
Check that:
- `ServiceCollectionExtensions` destructures the `LoadAll` tuple correctly — no silent discard of `latestVersions`
- `GenericHttpTrigger` is no longer a static class; `StartAsync` is an instance method; `IWorkflowDefinitionRegistry` is constructor-injected
- `GenericHttpTrigger` stores `versionedName` (not the raw `workflowName`) in the `__workflow` key of the envelope
- `DynamicWorkflowRunner.RunAsync` has the new `registry` parameter in position 4 (after `execCtx`)
- `RunSubOrchestration` checks `step.WorkflowVersion != null` first — the explicit-pin path must NOT call `ResolveVersionedName`
- `DispatchForeachSubOrch` applies the same precedence: explicit pin → direct construct; null → `ResolveVersionedName`
- `WrapSubOrchInput` receives the fully versioned name in both code paths
- `DynamicOrchestrationContextExtensions` passes `internalRegistry` as the 4th argument — not null, not the public interface

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the integration wiring changes for the versioning feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/versioning-spec.md`. This phase wires the versioned name through the HTTP trigger, orchestration runner, and DI setup.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions/Extensions/ServiceCollectionExtensions.cs`
> - `src/DeclarativeDurableFunctions/Functions/GenericHttpTrigger.cs`
> - `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs`
> - `src/DeclarativeDurableFunctions/Extensions/DynamicOrchestrationContextExtensions.cs`
>
> **Questions:**
> 1. Does `ServiceCollectionExtensions` destructure the tuple returned by `LoadAll` and pass both `definitions` and `latestVersions` to `WorkflowDefinitionRegistry`? If it discards `latestVersions`, the registry cannot resolve unversioned names.
> 2. Is `GenericHttpTrigger` no longer a static class, and is `StartAsync` an instance method? Static methods cannot receive constructor-injected dependencies in the isolated worker model.
> 3. Does `GenericHttpTrigger` store `versionedName` (not the raw HTTP route parameter `workflowName`) in the `__workflow` key? This is the version-pinning moment — the raw name must never be stored.
> 4. Does `DynamicWorkflowRunner.RunAsync` have `IWorkflowDefinitionRegistryInternal registry` as the fourth parameter?
> 5. In `RunSubOrchestration`: when `step.WorkflowVersion` is non-null, does it construct `"{step.WorkflowName}:{step.WorkflowVersion}"` directly without calling `ResolveVersionedName`? Calling `ResolveVersionedName` on an already-constructed versioned name would work (due to the passthrough rule), but direct construction is the intended explicit-pin path.
> 6. In `RunSubOrchestration`: when `step.WorkflowVersion` is null, does it call `registry.ResolveVersionedName(step.WorkflowName!)`?
> 7. Do both `RunSubOrchestration` and `DispatchForeachSubOrch` pass the resolved versioned name to `WrapSubOrchInput` — not the original `step.WorkflowName`?
> 8. Does `DynamicOrchestrationContextExtensions` pass the real `internalRegistry` instance (not null, not the public interface cast) as the 4th argument to `DynamicWorkflowRunner.RunAsync`?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Phase 4 — Tests

**What:** Test coverage for versioning. Two independent agents — registry/loader tests and runner tests can be written in parallel.

**Parallelism:** 2 agents running simultaneously.

### Agent 4A — Registry and Loader Tests
**Model:** Sonnet  
**Output:** `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowDefinitionRegistryTests.cs`

Two tasks in one file:

**Task 1 — Update existing tests.** Every existing test that constructs `WorkflowDefinitionRegistry` must be updated to:
- Pass both `definitions` and `latestVersions` dicts to the constructor
- Use versioned keys in the `definitions` dict (e.g., `"MyWorkflow:1"` not `"MyWorkflow"`)
- Add `Version = 1` to any `WorkflowDefinition` object literal
- Expect versioned names in any assertion on `WorkflowNames`

See §6.1 of `docs/versioning-spec.md` for the before/after pattern.

**Task 2 — New tests.** Implement all 18 tests specified in §6.2 of `docs/versioning-spec.md`.

### Agent 4B — Runner Tests
**Model:** Sonnet  
**Output:** `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowRunnerTests.cs`

Add the 4 new tests specified in §6.3 of `docs/versioning-spec.md`. These require a mock `IWorkflowDefinitionRegistryInternal` passed to `DynamicWorkflowRunner.RunAsync`. Use the existing mock infrastructure in the file — do not introduce new test helpers or base classes.

---

### Gate 4 — After Phase 4

#### Human review
Check that:
- All existing `WorkflowDefinitionRegistryTests` compile and pass — no old tests were silently removed
- The 18 new registry/loader tests from §6.2 are all present and named as specified
- The 4 new runner tests from §6.3 are all present
- The mock `IWorkflowDefinitionRegistryInternal` in runner tests verifies that `ResolveVersionedName` is called when `WorkflowVersion` is null and is NOT called when `WorkflowVersion` is set (explicit-pin tests)
- No test asserts on a mock setup value — each test verifies a real observable outcome

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the test changes for the versioning feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/versioning-spec.md`.
>
> Review the following files:
>
> - `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowDefinitionRegistryTests.cs`
> - `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowRunnerTests.cs`
>
> **Registry/loader test questions:**
> 1. Do all existing tests that construct `WorkflowDefinitionRegistry` pass two dicts (definitions + latestVersions) with versioned keys (e.g., `"MyWorkflow:1"`) in `definitions`?
> 2. Is `Registry_GetByUnversionedName_ReturnsLatestVersion` present? It must register v1 and v2 definitions and verify that `Get("MyWorkflow")` returns the v2 definition.
> 3. Is `Registry_ResolveVersionedName_LoopInnerNameWithEmbeddedColon_PassesThrough` present? It must verify that a `__loop__` name containing a colon passes through unchanged — no registry lookup.
> 4. Is `Registry_ResolveVersionedName_UnknownWorkflow_ThrowsWorkflowDefinitionException` present? It must verify that `WorkflowDefinitionException` is thrown (not `KeyNotFoundException`).
> 5. Are `LoadFromYaml_WithVersionField_ParsesVersion` and `LoadFromYaml_WithoutVersionField_DefaultsToVersion1` present?
> 6. Is `LoadFromYaml_VersionFieldLessThanOne_ThrowsWorkflowDefinitionException` present, and does it test `version: 0`?
>
> **Runner test questions:**
> 7. Are all 4 runner tests from §6.3 of the spec present: explicit-pin sub-orch, latest-resolve sub-orch, explicit-pin foreach, latest-resolve foreach?
> 8. In the explicit-pin tests (`WorkflowVersion = "1"`, latest is v2): does the test verify that `ResolveVersionedName` was NOT called on the mock registry, and that the `__workflow` envelope value is `"Sub:1"` (not `"Sub:2"`)?
> 9. In the latest-resolve tests (`WorkflowVersion = null`): does the test verify that `ResolveVersionedName` WAS called, and that the `__workflow` envelope value matches what the mock returns?
>
> **General test quality:**
> 10. Are test names descriptive of expected behavior (not implementation details)?
> 11. Are there any vacuous tests that assert on a value the mock was set up to return, rather than on a real observable outcome?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Phase 5 — Spec Doc Updates

**What:** Update `docs/spec.md` to reflect the versioning additions. No code changes — documentation only.

**Agent:** 1 agent  
**Model:** Haiku  
**Output:** `docs/spec.md` — update the six sections listed in §7 of `docs/versioning-spec.md`:

| Section | Change |
|---|---|
| §1.10 Workflow Name Resolution | Add `-vN` suffix stripping; document registry key format |
| §5.1 Top-Level Structure | Add `version:` to the YAML schema block |
| §5.5 Sub-orchestration Step | Add `version:` field to YAML schema block and rules table |
| §5.6 Foreach Step | Add `version:` field (applies only when `workflow:` is set) |
| §7.2 Internal Model Types — `WorkflowDefinition` | Add `Version` and `VersionedName` |
| §7.2 Internal Model Types — `StepDefinition` | Add `WorkflowVersion` |
| §7.1 Public Types — `IWorkflowDefinitionRegistry` | Add `ResolveVersionedName` to the interface |

Match the existing tone and formatting of `docs/spec.md` exactly.

No approval gate — if Gates 1–4 passed, this is a documentation pass.

---

## Model assignment summary

| Phase | Agent | Model | Reason |
|---|---|---|---|
| 1 — Models | 1 | Sonnet | Small but critical — wrong types cascade into all phases |
| 2A — Loader | 1 | Sonnet | YAML parsing + registry key generation; well-specified but has nuance in the loop context plumbing |
| 2B — Registry | 1 | Sonnet | Two files, well-specified; `ResolveVersionedName` passthrough rules require care |
| 3 — Integration wiring | 1 | Sonnet | Four files, shared data flow; must be internally consistent |
| 4A — Registry/loader tests | 1 | Sonnet | 18 new test cases + existing test updates |
| 4B — Runner tests | 1 | Sonnet | 4 new cases; mock verification logic has nuance |
| 5 — Spec doc updates | 1 | **Haiku** | Six section edits matching existing formatting; mechanical |
