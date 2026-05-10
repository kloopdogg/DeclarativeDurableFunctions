# Workflow Catalog Plan

## Status

Approved. Implementation not yet started.

This document describes the workflow catalog: a management plane for publishing, versioning, and starting workflow definitions stored in Azure Blob Storage and Azure Table Storage. It is an additive capability alongside the existing static file-based registry.

---

## What This Is Not

This is not a replacement for the static file-based registry. Developers who deploy YAML files with their Functions app continue to use the existing startup-loaded registry unchanged. The catalog is a separate feature for use cases where workflow definitions are authored and submitted at runtime — by a UI, an external system such as n8n, or an AI agent.

---

## Why the Catalog Is Needed

The static registry has two constraints that make it unsuitable for runtime-authored workflows:

- Definitions are loaded at host startup. A definition published after startup is not visible to any running worker without a restart.
- Workflow identity is tied to the YAML filename and the deploying app. There is no management API, no versioning, and no durable storage of submitted definitions.

The catalog removes both constraints:

- Definitions are published through a management endpoint and stored immediately in blob and table storage.
- Any worker can start a catalogued workflow immediately after it is published, with no restart and no cross-worker cache synchronization.
- Published definitions are versioned, immutable, and reusable across multiple starts.

The critical replay-safety constraint is unchanged: an orchestrator must not perform file I/O or network I/O. The catalog solves this by embedding a compiled workflow bundle snapshot in the orchestration input at start time — not by fetching definitions inside the orchestrator.

---

## Replay Safety Clarification

A common question is whether blob storage is needed for orchestration replay across worker restarts. It is not. The `DynamicWorkflowInvocation` envelope — including the full workflow bundle — lives in the orchestration input, which Durable Functions persists in orchestration history (Azure Table Storage, with automatic overflow to blob for large payloads). Replay reads the definition from history; no catalog lookup occurs. Blob storage is needed only to start new orchestration instances by name and version.

---

## Core Invariants

These rules are non-negotiable.

- Workflow versions are immutable.
- A workflow start binds to an exact `name + version`.
- The orchestration executes from a frozen workflow bundle snapshot captured at start time and embedded in orchestration input.
- Sub-orchestration references must resolve to exact versions before execution begins.
- No definition fetches occur inside the orchestrator.
- No `latest` resolution occurs inside the orchestrator.
- Any data needed to determine execution behavior must be present in orchestration input or event history before the orchestrator first yields.

---

## Decisions Made

These were open questions in earlier drafts. They are now resolved.

**Version format:** Server-assigned monotonically increasing integer strings per workflow name. Client-supplied versions are a future option.

**Child version syntax:** Step-level `workflowVersion` field on `sub-orchestration` and `foreach` steps. A top-level dependency map may be added later but is not required now.

**Storage placement:** Azure Storage implementation lives in the sample app first. Extract to a companion package only after the API stabilizes. The core library remains storage-agnostic.

**Bundle shape:** Pass the full bundle to all child orchestrations. Minimized child-specific bundles are a future optimization.

---

## Terminology

**Workflow Name** — a logical workflow identifier such as `OrderFulfillment`.

**Workflow Version** — an immutable version string for a specific published definition. Treated as an opaque string by the runtime.

**Workflow Reference** — the pair `(name, version)`.

**Workflow Bundle** — the compiled, immutable runtime snapshot needed to execute a workflow. Includes the root workflow definition and all child workflow definitions required for deterministic execution.

**Canonical YAML** — the original submitted YAML stored in blob storage.

**Compiled Snapshot** — a serialized runtime-friendly representation of the parsed definition bundle. Stored so the system does not need to parse YAML at orchestration time.

---

## Storage Model

### Blob Storage

- `workflows/{name}/{version}/definition.yaml` — the exact submitted YAML, for audit and download
- `workflows/{name}/{version}/bundle.json` — the compiled runtime snapshot used by the start endpoint
- `workflows/{name}/{version}/metadata.json` — optional convenience artifact; the catalog store is authoritative

### Metadata Catalog

Azure Table Storage for version indexing and concurrency control. Suggested fields:

- `WorkflowName`, `WorkflowVersion`, `Status`, `CreatedAtUtc`, `CreatedBy`
- `ContentHash`, `SchemaVersion`, `BlobPath`, `BundleBlobPath`
- `DisplayName`, `Description`, `Tags`
- `RootWorkflowName`, `DependencyReferences`
- `IsActive`, `SupersedesVersion`
- `ValidationErrors` (for rejected submissions if draft storage is desired)

---

## Versioning Rules

- Publishing the same `name + version` twice is rejected unless the content hash matches exactly (idempotent re-publish).
- Overwriting an existing version is forbidden.
- Starting by `name + version` always executes the exact published artifact.
- `latest` may exist as a management convenience but must not affect already-running orchestration instances.
- Orchestration input always records the exact `name + version` used.

---

## Management API Surface

### Publish Workflow

`POST /api/workflows`

Request: workflow name, YAML definition, optional display metadata, optional requested version.

Response: workflow name, workflow version, content hash, validation result, dependency summary, start URL.

Behavior: validate, parse, resolve child references, build bundle, persist canonical YAML + bundle + metadata, return immutable `name + version`.

### Start Workflow by Version

`POST /api/workflows/{name}/versions/{version}/start`

Request: business input payload, optional instance ID, optional correlation ID.

Response: Durable instance ID, workflow name, workflow version, status query URLs.

Behavior: load compiled bundle from blob outside the orchestrator, construct `DynamicWorkflowInvocation`, schedule the generic orchestrator.

### Convenience: Publish and Start

`POST /api/workflows/run`

Request: YAML definition, business input payload, optional correlation ID.

Response: workflow name, workflow version, Durable instance ID, status query URLs.

Behavior: publish the definition (same pipeline as `POST /api/workflows`), then immediately start an instance. Returns both the `name + version` (for reuse in future starts) and the instance ID. Callers such as n8n that remember the returned `name + version` can use `start` directly on subsequent runs without resubmitting the YAML.

### Read and Management Endpoints

- `GET /api/workflows` — list all workflows
- `GET /api/workflows/{name}` — list versions for a workflow
- `GET /api/workflows/{name}/versions/{version}` — get version metadata
- `GET /api/workflows/{name}/versions/{version}/yaml` — download canonical YAML
- `GET /api/workflows/{name}/versions/{version}/bundle` — download compiled bundle
- `POST /api/workflows/{name}/versions/{version}/disable`
- `POST /api/workflows/{name}/versions/{version}/enable`

---

## Runtime Contract

### DynamicWorkflowInvocation Envelope

```csharp
public sealed class DynamicWorkflowInvocation
{
    public WorkflowReference Workflow { get; init; } = default!;
    public WorkflowBundle Bundle { get; init; } = default!;
    public JsonElement Input { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed class WorkflowReference
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

public sealed class WorkflowBundle
{
    public string SchemaVersion { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public WorkflowReference Root { get; init; } = default!;
    public IReadOnlyDictionary<string, WorkflowDefinitionSnapshot> Definitions { get; init; }
        = new Dictionary<string, WorkflowDefinitionSnapshot>();
}
```

`Input` is the business payload exposed to expressions as `{{input}}`. `Bundle` contains the immutable execution definitions. `Workflow` identifies the root definition for the current orchestration instance. Dictionary keys use the format `{name}:{version}`.

This is intentionally separate from `WorkflowInput<TData>`, which is the activity and sub-orchestration envelope pattern already defined by the library.

### Generic Orchestrator

All catalog-started workflows route through a single generic orchestrator:

```csharp
public sealed class DeclarativeWorkflowOrchestrator
{
    [Function("DeclarativeWorkflow")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunDynamicWorkflowAsync();
}
```

The orchestrator function name is no longer the workflow name. The management start endpoint always schedules `DeclarativeWorkflow`.

### Runner Extension

```csharp
public static Task RunDynamicWorkflowAsync(this TaskOrchestrationContext context)
```

Reads `DynamicWorkflowInvocation` from orchestration input, builds `WorkflowExecutionContext` from `invocation.Input`, resolves the root definition from `invocation.Bundle`, and executes using the existing step dispatch logic.

### Sub-Orchestration Dispatch

When a catalog-sourced step launches a child workflow:

1. Resolve child `name + version` from the step definition.
2. Resolve the child definition from the same bundle.
3. Construct a child `DynamicWorkflowInvocation` with the same full bundle.
4. Call `context.CallSubOrchestratorAsync("DeclarativeWorkflow", childInvocation, options)`.

No external lookups. No registry access. The bundle is self-contained.

---

## Publish Pipeline

1. **Accept** — validate request shape, check name and YAML are present.
2. **Parse and validate** — YAML syntax, schema, step types, required fields, expression syntax, child workflow version references, size limits.
3. **Build bundle** — parse YAML into `WorkflowDefinition`, normalize into snapshot format, resolve child references, build dependency closure, compute content hash.
4. **Persist** — canonical YAML to blob, compiled bundle to blob, metadata to catalog.
5. **Return** — workflow name, version, content hash, publish timestamp, start route.

---

## Start Pipeline

1. **Resolve** — verify `name + version` exists and is startable; load compiled bundle from catalog or blob. This step happens outside the orchestrator.
2. **Build envelope** — construct `DynamicWorkflowInvocation` with bundle, business input, correlation data, optional instance ID.
3. **Schedule** — always schedule `DeclarativeWorkflow`, never a function named after the workflow.
4. **Return** — Durable instance ID, status query location, workflow name and version.

---

## Storage-Agnostic Interfaces

These live in the library as public extension points. Azure Storage implementations live in the sample app initially.

```csharp
public interface IWorkflowCatalog
{
    Task<WorkflowVersionRecord> PublishAsync(PublishWorkflowRequest request, CancellationToken cancellationToken);
    Task<WorkflowVersionRecord?> GetVersionAsync(string workflowName, string workflowVersion, CancellationToken cancellationToken);
    Task<WorkflowBundle> GetBundleAsync(string workflowName, string workflowVersion, CancellationToken cancellationToken);
}

public interface IWorkflowVersionAllocator
{
    Task<string> AllocateVersionAsync(string workflowName, CancellationToken cancellationToken);
}

public interface IWorkflowBundleBuilder
{
    Task<WorkflowBundle> BuildAsync(PublishWorkflowRequest request, CancellationToken cancellationToken);
}

public interface IWorkflowContentStore
{
    Task StoreYamlAsync(string workflowName, string workflowVersion, string yaml, CancellationToken cancellationToken);
    Task StoreBundleAsync(string workflowName, string workflowVersion, WorkflowBundle bundle, CancellationToken cancellationToken);
}
```

---

## Sub-Orchestration Versioning

`StepDefinition` gets a `WorkflowVersion` field (already added in the versioning feature). In catalog mode, every sub-orchestration and foreach step that references a child workflow must have an explicit version before the workflow can be published. The management endpoint validates this at publish time. No version resolution occurs inside the runner.

---

## Data Size Considerations

Embedding the bundle in orchestration input increases history size. Mitigations:

- Compile to a compact snapshot rather than raw YAML.
- Normalize to the minimal fields needed at runtime.
- Set conservative bundle size limits at publish time.
- Durable Functions automatically overflows large inputs to blob storage.
- Measure actual history growth in tests before optimizing.

---

## Validation Rules

Required at publish time:

- Unique step names where outputs are referenced
- Valid step types and required fields
- Valid `retry` configuration and ISO 8601 durations
- Valid switch cases and foreach source expressions
- Explicit child workflow versions for all sub-orchestration and foreach steps
- No duplicate `name + version` (reject unless content hash matches)
- No mutation of existing versions
- Bundle size under configured threshold
- Maximum nesting depth and dependency count under configured thresholds

Recommended additions:

- Activity names match a registered activity catalog
- Sub-orchestration references exist at publish time
- Circular dependency detection
- Expression complexity limits if definitions come from untrusted callers

---

## Security and Authorization

- Strong authentication for publish and management endpoints
- Authorization separating publish rights from start rights
- Audit fields: `CreatedBy`, `PublishedBy`, `StartedBy`
- Request logging with workflow name and version
- Payload size limits and rate limiting
- Input validation before storage
- Content scanning if AI-generated YAML is accepted from untrusted callers

---

## Observability

Structured telemetry for:

- Workflow publish success and failure, bundle build duration, bundle size
- Validation errors and rejected submissions
- Workflow start success and failure
- Durable instance ID to workflow reference correlation
- Child workflow version resolution

Each orchestration instance should record workflow name, version, bundle content hash, and correlation ID.

---

## Backward Compatibility

The static file-based registry is unchanged. Consumers who deploy YAML with their app continue to use `context.RunWorkflowAsync(registry)` with no changes. The catalog is a separate capability with its own entry points. No existing API surfaces are broken.

---

## Code Changes Required

### Library

- `src/DeclarativeDurableFunctions/Models/` — add `WorkflowReference`, `WorkflowBundle`, `WorkflowDefinitionSnapshot`, `DynamicWorkflowInvocation`
- `src/DeclarativeDurableFunctions/Engine/` — add `IWorkflowCatalog`, `IWorkflowVersionAllocator`, `IWorkflowBundleBuilder`, `IWorkflowContentStore`; add dynamic runner path to `WorkflowRunner` or a new `DynamicWorkflowRunner`
- `src/DeclarativeDurableFunctions/Functions/` — add `DeclarativeWorkflowOrchestrator`
- `src/DeclarativeDurableFunctions/Extensions/` — add `RunDynamicWorkflowAsync` extension method; update `ServiceCollectionExtensions` for catalog DI registration options

### Sample App

- Azure Blob Storage implementation of `IWorkflowContentStore`
- Azure Table Storage implementation of `IWorkflowCatalog` and `IWorkflowVersionAllocator`
- `WorkflowBundleBuilder` implementation
- HTTP endpoints: publish, start by version, run convenience, read/management endpoints
- End-to-end sample demonstrating publish then immediate start

---

## Implementation Plan

Versioning support (`StepDefinition.WorkflowVersion`, `WorkflowDefinition.Version`, loader and registry changes) is already merged and tested. The phases below build on that.

```
Phase 1 (Models)
  → Phase 2A (Runner + Orchestrator) || Phase 2B (Abstractions + Bundle Builder)  [parallel]
      → Phase 3A (Azure Storage) || Phase 3B (HTTP Endpoints)  [parallel]
          → Phase 4 (Child Version Validation)
              → Phase 5A (Unit Tests) || Phase 5B (Integration Tests)  [parallel]
```

Wall clock estimate with parallelism: ~4 hours. Sequential equivalent: ~7.5 hours.

---

## Phase 1 — Models

**What:** The four new contract types that every subsequent component builds against. Get them right before anything else runs.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Models/WorkflowReference.cs`
- `src/DeclarativeDurableFunctions/Models/WorkflowBundle.cs`
- `src/DeclarativeDurableFunctions/Models/WorkflowDefinitionSnapshot.cs`
- `src/DeclarativeDurableFunctions/Models/DynamicWorkflowInvocation.cs`

Exact shapes are specified in the Runtime Contract section above.

---

### Gate 1 — After Phase 1

#### Human review
Check that:
- `DynamicWorkflowInvocation` has `Workflow`, `Bundle`, `Input` (JsonElement), and `CorrelationId` — no extra fields
- `WorkflowBundle.Definitions` keys use `{name}:{version}` format
- `WorkflowDefinitionSnapshot` contains only the fields needed at runtime — no raw YAML, no audit metadata
- None of these types extend or reference `WorkflowInput<TData>` — they are intentionally separate contracts
- All properties use `init` setters, not `set`

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the new model types for the workflow catalog feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/workflow-catalog-plan.md`.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions/Models/WorkflowReference.cs`
> - `src/DeclarativeDurableFunctions/Models/WorkflowBundle.cs`
> - `src/DeclarativeDurableFunctions/Models/WorkflowDefinitionSnapshot.cs`
> - `src/DeclarativeDurableFunctions/Models/DynamicWorkflowInvocation.cs`
>
> **Questions:**
> 1. Does `DynamicWorkflowInvocation` contain exactly the four fields specified in the plan — `Workflow`, `Bundle`, `Input` (JsonElement), and `CorrelationId`? Are there any extra fields that should not be there?
> 2. Is `DynamicWorkflowInvocation` clearly separate from `WorkflowInput<TData>`? These are two distinct envelope contracts — conflating them would be a bug.
> 3. Does `WorkflowBundle.Definitions` use `{name}:{version}` as its key format? An unversioned key would break deterministic child resolution.
> 4. Does `WorkflowDefinitionSnapshot` contain only the runtime-necessary fields — step definitions and workflow metadata — without raw YAML text or catalog audit metadata?
> 5. Do all properties use `init` setters rather than mutable `set`? These types must be immutable once constructed.
> 6. Are there any fields typed as mutable collections (`List<T>`, `Dictionary<K,V>`) rather than read-only interfaces (`IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`)?
>
> Return a verdict: **Pass** or **Needs changes**, with a bullet list of specific issues.

---

## Phase 2 — Runner and Abstractions

**What:** The two independent components that depend on Phase 1 models but not on each other. Run them simultaneously.

**Parallelism:** 2 agents running simultaneously.

### Agent 2A — Runner and Generic Orchestrator
**Model:** **Opus** — replay safety, bundle threading through recursive dispatch, and correct async patterns all in one piece. Mistakes here are invisible to unit tests and only surface under Durable replay.  
**Output:**
- `src/DeclarativeDurableFunctions/Extensions/DynamicOrchestrationContextExtensions.cs` — add `RunDynamicWorkflowAsync` reading `DynamicWorkflowInvocation` from orchestration input
- `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs` — add catalog execution path: resolve root definition from `invocation.Bundle`, execute steps, forward bundle to child sub-orchestrations
- `src/DeclarativeDurableFunctions/Functions/DeclarativeWorkflowOrchestrator.cs` — `[Function("DeclarativeWorkflow")]` calling `RunDynamicWorkflowAsync`

Critical correctness requirements:
- No `Guid.NewGuid()`, no `DateTime.UtcNow`, no file or network I/O anywhere in the runner
- Sub-orchestration dispatch must always call `context.CallSubOrchestratorAsync("DeclarativeWorkflow", childInvocation, options)` — never a function named after the workflow
- Child `DynamicWorkflowInvocation` must carry the same full bundle as the parent
- Child definition resolved from `bundle.Definitions["{name}:{version}"]` — no registry lookup

### Agent 2B — Abstractions and Bundle Builder
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Engine/IWorkflowCatalog.cs`
- `src/DeclarativeDurableFunctions/Engine/IWorkflowVersionAllocator.cs`
- `src/DeclarativeDurableFunctions/Engine/IWorkflowBundleBuilder.cs`
- `src/DeclarativeDurableFunctions/Engine/IWorkflowContentStore.cs`
- `src/DeclarativeDurableFunctions/Engine/WorkflowBundleBuilder.cs` — storage-agnostic implementation: parse YAML → `WorkflowDefinition`, build `WorkflowDefinitionSnapshot`, walk step tree to find child references, assemble `WorkflowBundle`, compute content hash
- `src/DeclarativeDurableFunctions/Extensions/ServiceCollectionExtensions.cs` — add catalog DI registration options

Exact interface shapes are specified in the Storage-Agnostic Interfaces section above.

---

### Gate 2 — After Phase 2

**This is the most important gate.** The runner is the replay-safety boundary; the bundle builder is the correctness boundary for what gets executed.

#### Human review
Check that:
- Grep `DynamicWorkflowRunner.cs` for `Guid.NewGuid` — must return zero results
- Grep `DynamicWorkflowRunner.cs` for `DateTime.Now`, `DateTime.UtcNow`, `Random` — must return zero results
- Sub-orchestration dispatch in the catalog path calls `"DeclarativeWorkflow"`, never a dynamic function name
- Child `DynamicWorkflowInvocation` carries the same bundle instance as the parent (or an equivalent copy) — not null, not an empty bundle
- `WorkflowBundleBuilder` walks sub-orchestration and foreach steps to collect child references — not just the top-level steps
- Content hash is computed over the canonical bundle representation, not over the raw YAML string

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the runner and bundle builder for the workflow catalog feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/workflow-catalog-plan.md`.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs`
> - `src/DeclarativeDurableFunctions/Extensions/DynamicOrchestrationContextExtensions.cs`
> - `src/DeclarativeDurableFunctions/Functions/DeclarativeWorkflowOrchestrator.cs`
> - `src/DeclarativeDurableFunctions/Engine/WorkflowBundleBuilder.cs`
>
> **Replay safety (highest priority):**
> 1. Does `DynamicWorkflowRunner.cs` contain any call to `Guid.NewGuid()`? Any call to `DateTime.Now` or `DateTime.UtcNow`? Any file or network I/O? All of these are replay-safety violations.
> 2. When dispatching a child sub-orchestration in catalog mode, does the runner always call `context.CallSubOrchestratorAsync("DeclarativeWorkflow", ...)` — a fixed function name — rather than a dynamically constructed function name derived from the workflow name?
>
> **Bundle threading:**
> 3. Does the child `DynamicWorkflowInvocation` constructed for sub-orchestration dispatch carry the same `WorkflowBundle` as the parent invocation? A null or empty bundle would cause the child orchestration to fail on any worker that does not have the definition in a local registry.
> 4. Is the child definition resolved from `bundle.Definitions["{name}:{version}"]` — not from a registry lookup? A registry lookup inside the orchestrator is a correctness violation in catalog mode.
> 5. Does `RunDynamicWorkflowAsync` read the `DynamicWorkflowInvocation` from orchestration input, not from a DI-injected registry or any external source?
>
> **Bundle builder:**
> 6. Does `WorkflowBundleBuilder` walk the full step tree recursively to find child workflow references — including steps nested inside parallel blocks, foreach blocks, and switch cases? A shallow walk would miss nested sub-orchestrations.
> 7. Does the builder compute a content hash over the compiled bundle, not over the raw YAML string? The YAML string may have insignificant whitespace differences that would produce different hashes for semantically identical definitions.
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Phase 3 — Azure Storage and HTTP Endpoints

**What:** The sample app implementations and HTTP surface. Both depend on Phase 2 abstractions but are independent of each other.

**Parallelism:** 2 agents running simultaneously.

### Agent 3A — Azure Storage Implementation
**Model:** Sonnet  
**Output** (all in `src/DeclarativeDurableFunctions.TestApp/`):
- `Catalog/BlobWorkflowContentStore.cs` — implements `IWorkflowContentStore` using Azure Blob Storage SDK
- `Catalog/TableWorkflowCatalog.cs` — implements `IWorkflowCatalog` and `IWorkflowVersionAllocator` using Azure Table Storage SDK
- Wiring in `Program.cs` for DI registration

### Agent 3B — HTTP Endpoints
**Model:** Sonnet  
**Output** (all in `src/DeclarativeDurableFunctions.TestApp/`):
- `Functions/WorkflowManagementFunctions.cs` — all management HTTP endpoints: publish (`POST /api/workflows`), start by version (`POST /api/workflows/{name}/versions/{version}/start`), run convenience (`POST /api/workflows/run`), and read endpoints (`GET /api/workflows`, `GET /api/workflows/{name}`, `GET /api/workflows/{name}/versions/{version}`, `GET .../yaml`)

3B is written against the `IWorkflowCatalog`, `IWorkflowBundleBuilder`, and `IWorkflowContentStore` interfaces. It does not depend on 3A's concrete implementations.

---

### Gate 3 — After Phase 3

#### Human review
Check that:
- `POST /api/workflows/run` returns both `name + version` and Durable instance ID in the response body
- The start endpoint loads the bundle from blob **before** scheduling the orchestration — not inside a Durable activity
- The publish endpoint rejects duplicate `name + version` with a 409 unless the content hash matches exactly
- Azure Table Storage operations use partition key = `workflowName`, row key = `workflowVersion` — or a documented alternative with the same uniqueness guarantees
- No Azure SDK calls appear in the runner or orchestrator (Phase 2A output) — storage is sample-app only

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the Azure Storage implementations and HTTP endpoints for the workflow catalog feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/workflow-catalog-plan.md`.
>
> Review the following files:
>
> - `src/DeclarativeDurableFunctions.TestApp/Catalog/BlobWorkflowContentStore.cs`
> - `src/DeclarativeDurableFunctions.TestApp/Catalog/TableWorkflowCatalog.cs`
> - `src/DeclarativeDurableFunctions.TestApp/Functions/WorkflowManagementFunctions.cs`
>
> **Questions:**
> 1. Does `POST /api/workflows/run` return both a `name + version` identifier and a Durable instance ID? A caller who only gets the instance ID cannot reuse the definition later.
> 2. Does the start endpoint load the compiled bundle from blob storage **before** calling `StartNewAsync` on the Durable client — not inside a Durable activity? Loading inside an activity is safe but unnecessarily roundabout; loading before scheduling is the intended pattern.
> 3. Does the publish endpoint return 409 Conflict when a duplicate `name + version` is submitted with a different content hash? And accept (idempotently) a duplicate with the same content hash?
> 4. Does `TableWorkflowCatalog` use partition key = workflow name and row key = workflow version, ensuring uniqueness per `name + version` pair?
> 5. Are there any Azure SDK imports or calls in `DynamicWorkflowRunner.cs` or `DeclarativeWorkflowOrchestrator.cs`? There should be none — storage must not touch the orchestrator.
> 6. Does the start endpoint handle a not-found version gracefully — returning 404 rather than throwing an unhandled exception?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Phase 4 — Child Version Validation

**What:** Enforce that every sub-orchestration and foreach step in a catalog-submitted definition has an explicit `workflowVersion` before the bundle is accepted. One agent — the bundle builder and runner changes share the same data flow.

**Agent:** 1 agent  
**Model:** Sonnet  
**Output:**
- `src/DeclarativeDurableFunctions/Engine/WorkflowBundleBuilder.cs` — add validation pass: walk all steps recursively; throw `WorkflowDefinitionException` for any sub-orchestration or foreach step with a non-null `workflow` and a null `WorkflowVersion`
- `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs` — confirm child version construction uses `$"{step.WorkflowName}:{step.WorkflowVersion}"` directly (explicit pin, no `ResolveVersionedName` call) when both fields are set

---

### Gate 4 — After Phase 4

#### Human review
Check that:
- Validation walks nested steps inside parallel, foreach, and switch blocks — not just the top-level step list
- The exception message names the offending step and workflow so the caller knows what to fix
- The runner constructs child versioned names as `"{name}:{version}"` directly — no `ResolveVersionedName` call in the catalog path

---

## Phase 5 — Tests

**What:** Test coverage for the new catalog execution path. Both agents start simultaneously after Gate 4.

**Parallelism:** 2 agents running simultaneously.

### Agent 5A — Unit Tests
**Model:** Sonnet  
**Output:** `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowCatalogTests.cs`

Cases to cover:
- `WorkflowBundleBuilder` produces correct `Definitions` keys (`{name}:{version}`)
- `WorkflowBundleBuilder` walks nested steps and includes child definitions
- `WorkflowBundleBuilder` throws `WorkflowDefinitionException` when a sub-orchestration step has no `WorkflowVersion`
- `DynamicWorkflowRunner` resolves root definition from bundle, not registry
- `DynamicWorkflowRunner` passes full bundle to child `DynamicWorkflowInvocation`
- `DynamicWorkflowRunner` calls `"DeclarativeWorkflow"` for sub-orchestration dispatch — never a workflow-named function
- Duplicate publish with matching hash is accepted; with differing hash is rejected

### Agent 5B — Integration Tests
**Model:** Sonnet  
**Output:** `tests/DeclarativeDurableFunctions.Tests/Integration/WorkflowCatalogIntegrationTests.cs`

Cases to cover:
- Publish valid YAML → returns `name + version`; immediate start succeeds
- Run convenience endpoint → returns both `name + version` and instance ID
- Start non-existent version → 404
- Simulate publish on process A, execution on process B (no shared in-memory state) → execution succeeds because bundle is in orchestration input
- Bundle size over threshold → publish rejected

---

### Gate 5 — After Phase 5

#### Human review
Check that:
- The cross-process regression test (5B) genuinely shares no in-memory state between publish and execution — not just different class instances of the same registry
- Unit tests verify `"DeclarativeWorkflow"` is the scheduled function name — not just that `CallSubOrchestratorAsync` was called
- No test asserts on a mock setup value rather than a real observable outcome

#### Copilot review (somewhat-manual)

> **Prompt for GitHub Copilot (GPT-5.4):**
>
> Review the test suite for the workflow catalog feature in `DeclarativeDurableFunctions`. You have already read `AGENTS.md` and `docs/workflow-catalog-plan.md`.
>
> Review the following files:
>
> - `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowCatalogTests.cs`
> - `tests/DeclarativeDurableFunctions.Tests/Integration/WorkflowCatalogIntegrationTests.cs`
>
> **Unit test questions:**
> 1. Is there a test verifying that `WorkflowBundleBuilder` walks nested steps (inside parallel, foreach, and switch blocks) to collect child workflow references — not just top-level steps?
> 2. Is there a test verifying that a sub-orchestration step with no `WorkflowVersion` causes `WorkflowBundleBuilder` to throw `WorkflowDefinitionException`?
> 3. Is there a test verifying that `DynamicWorkflowRunner` dispatches sub-orchestrations to the fixed function name `"DeclarativeWorkflow"` — not to a name derived from the workflow name?
> 4. Is there a test verifying that the child `DynamicWorkflowInvocation` carries the same bundle as the parent — not null, not empty?
> 5. Is there a test verifying that the runner resolves the root definition from `invocation.Bundle`, not from a registry?
>
> **Integration test questions:**
> 6. Does the cross-process test genuinely isolate publish from execution — for example, two separate `WebApplicationFactory` instances or separate DI scopes with no shared in-memory state? Sharing an in-memory registry between publish and execution would make this test vacuous.
> 7. Is there a test for `POST /api/workflows/run` that asserts the response contains both `name + version` and a Durable instance ID?
>
> **General quality:**
> 8. Are test names descriptive of expected behavior?
> 9. Are there any vacuous tests that assert on a mock setup value rather than a real outcome?
>
> Return a verdict: **Pass** or **Needs changes**, with a numbered list of issues matching the question numbers above.

---

## Model Assignment Summary

| Phase | Agent | Model | Reason |
|---|---|---|---|
| 1 — Models | 1 | Sonnet | Well-specified data types; wrong shapes cascade into all phases |
| 2A — Runner + Orchestrator | 1 | **Opus** | Replay safety, bundle threading through recursive dispatch; mistakes are invisible to unit tests |
| 2B — Abstractions + Bundle Builder | 1 | Sonnet | Complex but well-specified; no replay constraints |
| 3A — Azure Storage | 1 | Sonnet | Mechanical SDK wiring against defined interfaces |
| 3B — HTTP Endpoints | 1 | Sonnet | Well-specified; written against interfaces from Phase 2B |
| 4 — Child Version Validation | 1 | Sonnet | Localized and well-specified; extends Phase 2B work |
| 5A — Unit Tests | 1 | Sonnet | Mechanical enumeration of specified cases |
| 5B — Integration Tests | 1 | Sonnet | Well-specified scenarios; cross-process isolation is the only subtle point |

---

## Test Plan

- Publish valid YAML returns immutable `name + version`
- Publish duplicate `name + version` is rejected unless content hash matches
- Run convenience endpoint returns both `name + version` and instance ID
- Start existing `name + version` schedules `DeclarativeWorkflow`
- Start non-existent `name + version` returns 404
- Runner executes from `DynamicWorkflowInvocation`
- Sub-orchestration launches `DeclarativeWorkflow` with child snapshot in input
- Workflow execution is deterministic with no external definition lookups
- A workflow published and started immediately succeeds without host restart
- Bundle size guardrails reject oversized definitions
- Child workflow version omission is rejected at publish time
- Simulate publish on one process, execution on another: verify success without prewarmed cache
