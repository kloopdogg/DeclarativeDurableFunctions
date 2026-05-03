# Dynamic Workflows Plan

## Status

Proposed design change.

This document describes the changes required to support dynamically managed workflows stored in Azure Blob Storage, published through a management path, and started immediately by `name + version`.

The chosen approach is:

- A management path publishes workflow YAML to blob storage and returns an immutable `name + version`.
- A separate start path starts a workflow by `name + version`.
- The runtime executes an immutable workflow snapshot captured at start time.
- The orchestrator never fetches YAML or metadata from blob storage.

This is the only design that is both replay-safe and capable of reliable immediate starts across scaled-out Azure Functions workers.

---

## Why This Change Is Needed

The current design assumes:

- Workflow YAML is part of the Functions app deployment.
- Workflow definitions are loaded at host startup and cached.
- Workflow identity comes from the YAML filename.
- The orchestrator function name matches the workflow name exactly.

Those assumptions are appropriate for static, checked-in workflows. They do not support:

- User- or AI-authored workflows created at runtime.
- Immediate starts of newly created workflows without app restart.
- Independent versioning of workflow definitions.
- Safe execution when different worker instances may handle publish, start, and orchestration replay.

The critical replay-safety constraint remains unchanged:

- An orchestrator must not perform file I/O or network I/O.
- Blob access from inside the orchestrator is not allowed.
- Looking up `latest` or fetching a definition by name during replay is not allowed.

Because of that, a design that merely stores YAML in blob and resolves it by name later inside the runner is not sufficient.

---

## Decision Summary

We will implement a management-plane and runtime-plane split.

Management plane:

- Accept YAML submissions.
- Validate and parse definitions.
- Assign immutable versions.
- Persist canonical YAML and compiled runtime snapshots.
- Resolve dependency versions.
- Return `name + version`.
- Start workflows by `name + version`.

Runtime plane:

- Use a single generic orchestrator function for dynamic workflows.
- Start the orchestrator with an immutable workflow snapshot included in the orchestration input.
- Execute only from that snapshot during the orchestration lifetime.
- Pass child workflow snapshots into sub-orchestrations deterministically.

This means blob storage is the source of truth, but orchestrations execute from a frozen snapshot, not from a live lookup.

---

## Goals

- Support publishing workflow YAML at runtime through an authenticated management endpoint.
- Return an immutable `workflowName` and `workflowVersion`.
- Support immediate starts by `workflowName + workflowVersion`.
- Preserve Durable Functions replay determinism.
- Support scaled-out Azure Functions workers without requiring synchronized in-memory caches.
- Preserve current declarative execution semantics.
- Keep the core library storage-agnostic where possible.

---

## Non-Goals

- Supporting `start latest version` inside the orchestrator.
- Allowing mutable in-place edits of an existing version.
- Allowing the orchestrator to resolve definitions from blob, table storage, or any external system at runtime.
- Solving visual workflow authoring.
- Designing a multi-tenant billing or quota model in this pass.

---

## Core Invariants

These rules are non-negotiable.

- Workflow versions are immutable.
- A workflow start binds to an exact `name + version`.
- The orchestration executes from a frozen workflow snapshot captured at start time.
- Sub-orchestration references must also resolve to exact versions before execution.
- No runtime definition fetches occur inside the orchestrator.
- No `latest` resolution occurs inside the orchestrator.
- Any data needed to choose execution behavior must already be present in orchestration input or event history.

---

## Key Architectural Change

### Current Model

Current model:

- `IWorkflowDefinitionRegistry` exposes a process-local in-memory catalog.
- `RunWorkflowAsync` assumes the workflow can be resolved from the registry.
- Workflow identity is coupled to the orchestrator function name.

That model works for static deployments only.

### New Model

New model:

- Dynamic workflows use a single generic orchestrator function, for example `DeclarativeWorkflow`.
- The start endpoint resolves the requested `name + version` outside the orchestrator.
- The start endpoint includes a compiled workflow bundle snapshot in the orchestration input.
- The runner executes against that bundle snapshot.
- The registry remains useful for static-file mode, but dynamic mode must not depend on registry availability on a given worker instance.

This removes the cross-worker race where one instance knows about a newly published workflow but another instance does not.

---

## Why a Snapshot Is Required for Immediate Starts

A local cache warm-up is not enough.

Example failure mode:

1. Worker A receives `POST /workflows` and stores YAML in blob.
2. Worker A warms its local in-memory registry.
3. Worker A receives `POST /workflows/{name}/versions/{version}/start` and schedules the orchestration.
4. The orchestration executes on Worker B.
5. Worker B does not have the new definition in memory.
6. If Worker B fetches from blob during orchestration execution, replay safety is violated.

To avoid this, the orchestration input must contain an immutable compiled definition snapshot. That makes the execution self-contained and deterministic.

---

## Terminology

Workflow Name:

- A logical workflow identifier such as `OrderFulfillment`.

Workflow Version:

- An immutable version string for a specific published definition.
- This may be an integer, timestamp-based string, or semantic version string.
- The system should treat it as an opaque string.

Workflow Reference:

- The pair `(name, version)`.

Workflow Bundle:

- The compiled, immutable runtime snapshot needed to execute a workflow.
- Includes the root workflow definition and all child workflow definitions required for deterministic execution.

Canonical YAML:

- The original submitted YAML stored in blob storage.

Compiled Snapshot:

- A serialized runtime-friendly representation of the parsed definition bundle.
- Stored so the system does not need to parse YAML at orchestration time.

---

## Recommended Storage Model

Use Azure Blob Storage for content and a metadata catalog for indexing.

### Blob Storage

Store canonical YAML and compiled snapshots in blob storage.

Recommended layout:

- `workflows/{name}/{version}/definition.yaml`
- `workflows/{name}/{version}/bundle.json`
- `workflows/{name}/{version}/metadata.json`

`definition.yaml`:

- The exact submitted YAML.
- Used for audit, download, and re-validation.

`bundle.json`:

- The compiled runtime snapshot for execution.
- Used by the start endpoint.
- Must include the root workflow and any required dependencies.

`metadata.json`:

- Optional convenience artifact for inspection.
- The authoritative metadata should still live in a catalog store.

### Metadata Catalog

Use a catalog store for fast lookup and concurrency control.

Recommended choices:

- Azure Table Storage for simplicity.
- Cosmos DB only if richer querying or higher-scale management workflows are needed.

Suggested metadata fields:

- `WorkflowName`
- `WorkflowVersion`
- `Status`
- `CreatedAtUtc`
- `CreatedBy`
- `ContentHash`
- `SchemaVersion`
- `BlobPath`
- `BundleBlobPath`
- `DisplayName`
- `Description`
- `Tags`
- `RootWorkflowName`
- `DependencyReferences`
- `IsActive`
- `SupersedesVersion`
- `ValidationErrors` for failed drafts if draft storage is desired

---

## Versioning Rules

Versions must be immutable.

Required rules:

- Publishing the same `name + version` twice is rejected unless content hash matches exactly and the operation is explicitly idempotent.
- Overwriting an existing version is forbidden.
- Starting by `name + version` always means the exact published artifact.
- `Latest` may exist as a management convenience, but it must not affect already-started orchestration instances.
- The orchestrator input must always record the exact version used.

Recommended first implementation:

- Use server-assigned monotonically increasing version strings per workflow name.
- Allow a client-supplied version later if needed.
- Persist a content hash for deduplication and audit.

---

## Management API Surface

### Publish Workflow

`POST /workflows`

Request:

- Workflow name
- YAML definition
- Optional display metadata
- Optional requested version
- Optional publish options

Response:

- Workflow name
- Workflow version
- Content hash
- Validation result
- Dependency summary
- Start URL or route template

Behavior:

- Validate request shape.
- Parse YAML.
- Validate schema.
- Validate expressions where possible.
- Resolve child workflow references and versions.
- Build the runtime bundle.
- Persist canonical YAML, bundle, and metadata.
- Return the final immutable `name + version`.

### Start Workflow

`POST /workflows/{name}/versions/{version}/start`

Request:

- Business input payload
- Optional orchestration instance ID
- Optional correlation ID
- Optional start time
- Optional tags or request metadata

Response:

- Durable instance ID
- Workflow name
- Workflow version
- Status query URLs if using HTTP management conventions

Behavior:

- Load metadata and compiled bundle outside the orchestrator.
- Reject start if the workflow version does not exist or is not startable.
- Construct the orchestration input envelope.
- Schedule the generic orchestrator with that envelope.

### Read APIs

Recommended management endpoints:

- `GET /workflows`
- `GET /workflows/{name}`
- `GET /workflows/{name}/versions/{version}`
- `GET /workflows/{name}/versions/{version}/yaml`
- `GET /workflows/{name}/versions/{version}/bundle`
- `POST /workflows/{name}/versions/{version}/disable`
- `POST /workflows/{name}/versions/{version}/enable`

These are management-plane conveniences, not runtime requirements.

---

## Runtime Contract Changes

## New Top-Level Invocation Envelope

Add a new orchestration input contract for dynamic workflows.

Suggested shape:

```csharp
public sealed class DynamicWorkflowInvocation
{
    public WorkflowReference Workflow { get; init; } = default!;
    public WorkflowBundle Bundle { get; init; } = default!;
    public JsonElement Input { get; init; }
    public string? CorrelationId { get; init; }
}
```

Suggested supporting types:

```csharp
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

Notes:

- `Input` is the business payload exposed to expressions as `{{input}}`.
- `Bundle` contains the immutable execution definitions.
- `Workflow` identifies the root definition used for the current orchestration instance.

This is intentionally separate from `WorkflowInput<TData>`, which is the activity/sub-orchestration envelope pattern already defined by the library.

---

## Workflow Bundle Design

The workflow bundle must contain everything needed to execute deterministically.

Minimum bundle contents:

- Root workflow reference
- Root workflow definition snapshot
- All child workflow definition snapshots reachable through sub-orchestration steps
- Bundle content hash
- Bundle schema version
- Dependency map

A good key format for `Definitions` is:

- `{name}:{version}`

The runner should never need to resolve a workflow outside this bundle once the orchestration has started.

---

## Sub-Orchestration Versioning

Dynamic sub-orchestrations need a versioning story.

Current `StepDefinition` only carries a workflow name. That is insufficient for deterministic dynamic execution because a child workflow name alone is ambiguous.

We need one of these options:

### Option A: Step-Level Version Field

Add a raw string field to `StepDefinition`:

- `WorkflowVersion`

YAML shape:

```yaml
- name: ValidateChild
  type: sub-orchestration
  workflow: OrderValidation
  workflowVersion: "3"
```

Pros:

- Explicit and simple.
- Easy to validate.
- Easy to serialize into the runtime model.

Cons:

- Repetitive when many child references share a versioning policy.

### Option B: Top-Level Dependency Map

Add a top-level dependencies block:

```yaml
workflow:
  name: OrderFulfillment
  dependencies:
    OrderValidation: "3"
    FulfillLineItem: "12"
```

Then child steps reference only the name.

Pros:

- Cleaner YAML for large workflows.
- Centralized dependency version pinning.

Cons:

- More loader complexity.
- Less local readability at the step site.

### Recommendation

Implement Option A first.

Reason:

- It is the smallest, most explicit change.
- It keeps dependency resolution local and simple.
- It is easier to review and test.
- A higher-level dependency map can be added later without breaking the core runtime model.

Required rule:

- In dynamic mode, every sub-orchestration step must resolve to an exact child version before a workflow can be published.
- No child version resolution may happen inside the runner.

---

## Generic Orchestrator Requirement

Dynamic workflows require a single generic orchestrator function.

Suggested pattern:

```csharp
public sealed class DeclarativeWorkflowOrchestrator
{
    [Function("DeclarativeWorkflow")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunDynamicWorkflowAsync();
}
```

Important consequences:

- The orchestrator function name is no longer the workflow name.
- `docs/spec.md` rules tying the orchestrator function name to the YAML filename must be revised.
- The management start endpoint always schedules `DeclarativeWorkflow`, not a per-workflow orchestrator.

Static mode can continue to support named orchestrator stubs if desired, but dynamic mode must not depend on them.

---

## Runner Changes

The runner needs a dynamic-mode execution path.

### New Extension Method

Add a new extension method, for example:

```csharp
public static Task RunDynamicWorkflowAsync(this TaskOrchestrationContext context)
```

or:

```csharp
public static Task RunWorkflowAsync(
    this TaskOrchestrationContext context,
    DynamicWorkflowInvocation invocation)
```

Recommended behavior:

- Read the `DynamicWorkflowInvocation` from orchestration input.
- Build the `WorkflowExecutionContext` using `invocation.Input`.
- Resolve the root workflow definition from `invocation.Bundle`.
- Execute using the existing step dispatch logic.

### Sub-Orchestration Dispatch

When a step launches a child workflow:

- Resolve the child `name + version` from the current step definition.
- Resolve the child definition from the same bundle.
- Create a child `DynamicWorkflowInvocation`.
- Pass the same bundle or a minimized child bundle into the sub-orchestration input.
- Call `context.CallSubOrchestratorAsync("DeclarativeWorkflow", childInvocation, options)`.

This preserves determinism and allows child workflows to run without external lookups.

### Bundle Passing Strategy

Two choices:

- Pass the full bundle to every child orchestration.
- Pass a minimized child bundle containing only the child closure.

Recommendation:

- Start with passing the full bundle.
- Optimize later if bundle size becomes an issue.

Reason:

- It is much simpler.
- It avoids subtle missing-dependency bugs.
- The primary goal is correctness.

---

## Registry and Provider Changes

The existing registry should not disappear, but its role changes.

### Keep Static Registry Support

Retain the current startup-loaded registry for static, file-based workflows.

This supports:

- Sample app scenarios
- Local development
- Existing plan phases
- Simpler deployment-based workflow management

### Add Dynamic Management Abstractions

Introduce storage-agnostic interfaces for dynamic workflow management.

Suggested interfaces:

```csharp
public interface IWorkflowCatalog
{
    Task<WorkflowVersionRecord> PublishAsync(PublishWorkflowRequest request, CancellationToken cancellationToken);
    Task<WorkflowVersionRecord?> GetVersionAsync(string workflowName, string workflowVersion, CancellationToken cancellationToken);
    Task<WorkflowBundle> GetBundleAsync(string workflowName, string workflowVersion, CancellationToken cancellationToken);
}
```

```csharp
public interface IWorkflowVersionAllocator
{
    Task<string> AllocateVersionAsync(string workflowName, CancellationToken cancellationToken);
}
```

```csharp
public interface IWorkflowBundleBuilder
{
    Task<WorkflowBundle> BuildAsync(PublishWorkflowRequest request, CancellationToken cancellationToken);
}
```

```csharp
public interface IWorkflowContentStore
{
    Task StoreYamlAsync(string workflowName, string workflowVersion, string yaml, CancellationToken cancellationToken);
    Task StoreBundleAsync(string workflowName, string workflowVersion, WorkflowBundle bundle, CancellationToken cancellationToken);
}
```

Notes:

- These interfaces should live in the library if they are intended public extension points.
- Blob and table implementations can live in the sample app first, or later be moved into an optional Azure Storage integration package.
- Do not force Azure Storage dependencies into the core runtime engine unless necessary.

---

## Publish Pipeline

Publishing a workflow should follow a strict pipeline.

### Step 1: Accept Request

Input:

- Workflow name
- YAML text
- Optional requested version
- Optional publish metadata

### Step 2: Parse and Validate

Validate:

- YAML is syntactically valid.
- Schema is valid.
- Step types are valid.
- Required fields are present.
- Expressions are syntactically valid where validation is possible.
- All referenced child workflows include explicit versions in dynamic mode.
- No invalid cycles exist unless recursive workflows are intentionally supported.
- Size limits are respected.

### Step 3: Build Compiled Snapshot

Transform:

- Parse YAML into `WorkflowDefinition`.
- Normalize into the internal snapshot format.
- Resolve child workflow references into exact `name + version`.
- Build the dependency closure.
- Produce a `WorkflowBundle`.
- Compute a content hash for the bundle.

### Step 4: Persist

Persist atomically as far as practical:

- Canonical YAML to blob
- Compiled bundle to blob
- Metadata to the catalog

### Step 5: Return Result

Return:

- Workflow name
- Workflow version
- Content hash
- Publish timestamp
- Start route information

---

## Start Pipeline

Starting a workflow should follow this sequence.

### Step 1: Resolve Version

The start endpoint receives `name + version`.

It must:

- Verify the version exists.
- Verify it is startable.
- Load the compiled bundle from the catalog or blob outside the orchestrator.

### Step 2: Build Invocation Envelope

Construct:

- `WorkflowReference`
- `WorkflowBundle`
- Business input payload
- Correlation data
- Optional instance ID

### Step 3: Schedule Generic Orchestrator

Always schedule the generic orchestrator function name, for example `DeclarativeWorkflow`.

Never schedule an orchestrator whose function name is derived from the workflow name in dynamic mode.

### Step 4: Return Durable Status

Return:

- Durable instance ID
- Query status location
- Workflow name and version used

---

## Immediate Start Semantics

Immediate start means:

- A workflow published through the management endpoint can be started as soon as the publish call returns successfully.
- No host restart is required.
- No cache propagation delay is required.
- No warm-up across all worker instances is required.

The design achieves this by moving all required execution state into the orchestration start input.

It does not achieve this by trying to synchronize in-memory registries across workers.

---

## Data Size and History Considerations

Embedding the workflow bundle in orchestration input increases orchestration history size.

This is the main tradeoff of the design.

Mitigations:

- Store a compiled, compact snapshot rather than raw YAML.
- Normalize the snapshot to the minimal fields needed at runtime.
- Set conservative bundle size limits.
- Prefer explicit dependencies and smaller workflow definitions.
- Measure real history growth in tests.
- Optimize later by passing minimized child bundles if needed.

This tradeoff is acceptable because correctness and replay safety are higher priority than minimizing input size in the first implementation.

---

## Validation Rules

The management path should reject definitions early.

Required validations:

- Unique workflow step names where outputs are referenced
- Valid step types
- Valid `retry` configuration
- Valid ISO 8601 durations
- Valid switch cases
- Valid foreach source expressions
- Explicit child workflow versions in dynamic mode
- No duplicate `name + version`
- No mutation of existing versions
- Bundle size under configured threshold
- Maximum nesting depth under configured threshold
- Maximum dependency count under configured threshold

Recommended additional validations:

- Activity names match an allowlist or registered activity catalog
- Sub-orchestration references exist at publish time
- Circular dependency detection
- Expression complexity limits if untrusted authors are allowed

---

## Security and Authorization

Management endpoints are a high-risk surface.

Minimum requirements:

- Strong authentication for publish and administrative endpoints
- Authorization separating publish rights from start rights
- Audit fields for `CreatedBy`, `PublishedBy`, and `StartedBy`
- Request logging with workflow name and version
- Payload size limits
- Rate limiting
- Input validation before storage
- Optional content scanning if AI-generated YAML is accepted from untrusted callers

Recommended separation:

- Management endpoints require stronger privileges.
- Start endpoints may be exposed to application clients under narrower rules.
- Blob storage should not be directly writable by clients.

---

## Observability

Add structured telemetry for:

- Workflow publish success and failure
- Workflow start success and failure
- Bundle build duration
- Bundle size
- Validation errors
- Durable instance ID to workflow reference correlation
- Child workflow reference resolution
- Rejected publish attempts
- Rejected duplicate version attempts

Each orchestration instance should record:

- Workflow name
- Workflow version
- Bundle content hash
- Correlation ID

---

## Backward Compatibility

We should preserve the existing static-file mode.

Recommended compatibility strategy:

- Keep static registry-based execution for workflows deployed with the app.
- Add dynamic mode as a parallel capability.
- Do not break `WorkflowInput<TData>` or `WorkflowMetadata`.
- Avoid forcing all consumers onto blob storage.
- Keep the current file-based sample path working while adding a new dynamic workflow sample.

This lets the project support both:

- Static app-packaged workflows
- Dynamically published workflows

---

## Proposed Documentation Changes

Update these documents:

- `docs/vision.md`
- `docs/spec.md`
- `README.md`
- `docs/plan.md`

Required changes:

- Replace the assumption that workflow identity comes from the YAML filename in dynamic mode.
- Replace the assumption that YAML files are always part of the Functions app.
- Document the generic orchestrator model for dynamic workflows.
- Document exact version pinning for sub-orchestrations in dynamic mode.
- Document publish and start management endpoints.
- Document the immutable bundle snapshot design and why it exists.

---

## Code Changes Required

## Library Contracts

Files likely to change:

- `src/DeclarativeDurableFunctions/Models/StepDefinition.cs`
- `src/DeclarativeDurableFunctions/Models/WorkflowDefinition.cs`
- `src/DeclarativeDurableFunctions/Engine/IWorkflowDefinitionRegistry.cs`
- `src/DeclarativeDurableFunctions/Extensions/OrchestrationContextExtensions.cs`
- `src/DeclarativeDurableFunctions/Extensions/ServiceCollectionExtensions.cs`

Expected contract changes:

- Add workflow reference types
- Add dynamic invocation envelope types
- Add child workflow version support
- Add dynamic runner entry point
- Add management/catalog abstractions
- Expand options beyond `WorkflowsDirectory`

## Engine

Files likely to change:

- `src/DeclarativeDurableFunctions/Engine/WorkflowRunner.cs`
- `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionLoader.cs`
- `src/DeclarativeDurableFunctions/Engine/WorkflowExecutionContext.cs`

Expected engine changes:

- Execute from a `WorkflowBundle`
- Support generic dynamic sub-orchestration calls
- Carry `workflowName + version` in execution metadata
- Preserve existing expression semantics

## Sample App

Expected additions:

- Publish endpoint
- Start-by-name-version endpoint
- Generic orchestrator
- Blob storage implementation
- Catalog implementation
- End-to-end sample for dynamic workflows

---

## Recommended Implementation Order

### Phase 1: Spec Update

- Update the design docs to introduce dynamic mode.
- Explicitly preserve replay-safety requirements.
- Decide on child workflow version syntax.

### Phase 2: Runtime Contract

- Add `WorkflowReference`, `WorkflowBundle`, and `DynamicWorkflowInvocation`.
- Add runner support for executing from a bundle.
- Add a generic orchestrator path.

### Phase 3: Management Abstractions

- Add storage-agnostic catalog, content store, and bundle builder interfaces.
- Add options and DI registration for dynamic mode.

### Phase 4: Sample Azure Storage Implementation

- Implement blob-backed content storage.
- Implement metadata catalog.
- Implement version allocation.
- Implement publish/start endpoints.

### Phase 5: Sub-Orchestration Versioning

- Add step-level child workflow version support.
- Resolve and validate dependency closures at publish time.
- Pass child invocation bundles deterministically.

### Phase 6: Tests

- Unit tests for bundle building
- Unit tests for publish validation
- Unit tests for dynamic runner path
- Unit tests for child workflow version resolution
- Integration tests for publish then immediate start
- Integration tests across multiple worker instances if feasible
- Regression tests proving no blob access occurs inside orchestrator execution

---

## Test Plan

Required tests:

- Publish valid YAML returns immutable `name + version`
- Publish duplicate `name + version` is rejected
- Start existing `name + version` schedules the generic orchestrator
- Start non-existent `name + version` returns not found
- Runner executes from `DynamicWorkflowInvocation`
- Sub-orchestration launches `DeclarativeWorkflow` with child snapshot input
- Workflow execution is deterministic with no external definition lookups
- A workflow published and started immediately succeeds without host restart
- Bundle size guardrails reject oversized definitions
- Child workflow version omission is rejected in dynamic mode

Important regression test:

- Simulate publish on one process and orchestration execution on another process with no prewarmed cache.
- Verify execution still succeeds because the bundle is supplied in orchestration input.

---

## Open Decisions

These should be resolved before implementation begins.

### Version Format

Choose one:

- Server-assigned integer string
- Timestamp-based string
- Semantic version string
- Client-supplied opaque string with collision checks

Recommendation:

- Start with server-assigned integer strings.

### Child Version Syntax

Choose one:

- Step-level `workflowVersion`
- Top-level dependency map

Recommendation:

- Start with step-level `workflowVersion`.

### Storage Placement

Choose one:

- Azure Storage implementation inside the sample app first
- Optional companion package for Azure integration
- Azure dependencies directly in the core package

Recommendation:

- Keep the core package storage-agnostic.
- Put Azure Storage implementation in the sample app first.
- Extract to a companion package only after the API stabilizes.

### Bundle Shape

Choose one:

- Full bundle passed to all child workflows
- Minimized child-specific bundle

Recommendation:

- Start with full bundle for correctness.

---

## Explicit Spec Changes Needed

The following current assumptions must be revised.

- Workflow name is the YAML filename without extension must become static mode only.
- The orchestrator function name must exactly match the YAML filename must become static mode only.
- YAML workflow files are part of the Functions app must become one supported mode, not the only mode.
- `RunWorkflowAsync(registry)` must no longer be the only orchestration entry point.
- Child workflows need explicit version pinning in dynamic mode.

---

## Final Recommendation

Implement dynamic workflows as a separate, explicit execution mode with these properties:

- Management endpoints publish immutable named and versioned workflows.
- Blob storage holds canonical YAML and compiled bundle artifacts.
- A metadata catalog indexes versions and content hashes.
- The start endpoint resolves `name + version` outside the orchestrator.
- The orchestration input carries a deterministic workflow bundle snapshot.
- A single generic orchestrator executes all dynamic workflows.
- Sub-orchestrations also execute through the generic orchestrator using exact child versions.

Do not implement dynamic workflows as look up YAML in blob by name during execution. That design is incompatible with Durable replay safety and will fail under scale-out.

The management path plus immutable snapshot approach is the correct design for immediate starts.