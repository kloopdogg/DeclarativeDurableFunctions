# Versioning Spec: DeclarativeDurableFunctions

> This spec defines the precise changes required to add workflow versioning. An agent should be able to execute these changes without additional clarification. Read `docs/vision.md` §Workflow Versioning and `docs/spec.md` for full context before starting.

---

## 1. Issues and Decisions

These are resolved. Do not relitigate them.

### 1.1 Registry Key Format

Versioned names use the format `"{baseName}:{N}"` — for example, `"OrderFulfillment:2"`. The colon is unambiguous as a separator because file names cannot contain colons on Windows/Azure. This format exactly matches the `{name}:{version}` key convention specified in `docs/dynamic-workflows-plan.md` for bundle `Definitions` dicts.

### 1.2 YAML Field for Sub-orchestration Version Pinning

The YAML field for pinning a sub-orchestration or foreach-workflow to an exact version is `version:` (not `workflowVersion:`). The step already declares `workflow: FulfillLineItem`, so `version:` is unambiguous in context and matches the top-level `workflow.version:` field name.

### 1.3 Base Name Source

The base workflow name (registry key prefix) comes from the YAML **file name** with any `-vN` suffix stripped — not from the YAML `workflow.name:` field. The `name:` field continues to be `DisplayName` only. This is consistent with §1.10 of `docs/spec.md`.

### 1.4 Default Version

If `workflow.version:` is absent from the YAML, the version defaults to `1`. Existing YAML files without the field are treated as v1. This is a non-breaking change — all current workflows continue to work unchanged and are simply registered as `"WorkflowName:1"`.

### 1.5 `__loop__` Inner Workflow Names Embed the Versioned Parent Name

Loop steps generate a synthetic inner workflow registered as `"__loop__{versionedName}__{stepName}"`. This is achieved by passing the versioned workflow name (e.g., `"OrderFulfillment:2"`) as the `workflowContext` parameter through `ParseSteps` → `ParseStep`. For example, a loop step named `"StatusLoop"` in `OrderFulfillment-v2.yaml` produces the inner workflow key `"__loop__OrderFulfillment:2__StatusLoop"`. This prevents v1 and v2 loop steps with the same name from colliding in the registry.

### 1.6 `ResolveVersionedName` Passthrough Rules

`ResolveVersionedName(string workflowName)` applies these rules in order:

1. If `workflowName.Contains(':')` → return `workflowName` unchanged. This handles both already-versioned names (`"OrderFulfillment:2"`) and loop inner workflow names (`"__loop__OrderFulfillment:2__StatusLoop"` — which contains the embedded colon).
2. Otherwise → look up `workflowName` in `_latestVersions` and return `"{workflowName}:{latest}"`. Throw `WorkflowDefinitionException` if not found.

No separate `StartsWith("__")` check is needed — the embedded colon in `__loop__` names covers them automatically.

### 1.7 `ResolveVersionedName` on the Public Interface

`ResolveVersionedName` is added to the **public** `IWorkflowDefinitionRegistry` interface. Rationale: `GenericHttpTrigger` (a framework class) needs it without casting to the internal interface, and any consumer building a custom trigger needs it too. The future Azure Storage registry will implement the same contract.

### 1.8 Sub-orchestration Version Resolution Precedence

At dispatch time, `DynamicWorkflowRunner` resolves a sub-orchestration or foreach-workflow step's versioned name using this precedence:

1. If `step.WorkflowVersion` is non-null: construct `"{step.WorkflowName}:{step.WorkflowVersion}"` directly. No registry lookup. This is the explicit-pin path.
2. If `step.WorkflowVersion` is null: call `registry.ResolveVersionedName(step.WorkflowName!)`. This resolves to the current latest version at the moment the step first executes — correct for workflows that always want the latest, but a latent risk when a sub-orchestration has breaking changes and the parent is mid-flight.

### 1.9 `GenericHttpTrigger` Must Become an Instance Class

Currently `GenericHttpTrigger.StartAsync` is static and has no constructor injection. To resolve workflow names before scheduling, the method must be non-static and `IWorkflowDefinitionRegistry` must be injected via the constructor. The isolated worker model supports DI-constructed function classes.

### 1.10 `StepDefinition.WorkflowVersion` Is a `string?`

The version pin on a step is stored as `string?` (not `int?`) for forward compatibility with the dynamic workflows plan, where version strings may be opaque (e.g., timestamps or server-assigned IDs, not necessarily integers). For file-based YAML, the value will always be a numeric string like `"2"`. When constructing the versioned name: `$"{WorkflowName}:{WorkflowVersion}"` produces `"FulfillLineItem:2"`, which matches the registry key format.

### 1.11 `LoadAll` Return Type Changes to a Tuple

`WorkflowDefinitionLoader.LoadAll` currently returns `IReadOnlyDictionary<string, WorkflowDefinition>`. It must return a tuple `(IReadOnlyDictionary<string, WorkflowDefinition> Definitions, IReadOnlyDictionary<string, int> LatestVersions)`. `ServiceCollectionExtensions` is the only caller and must be updated to destructure the tuple. The `LatestVersions` dict maps base name → highest registered version integer.

### 1.12 Internal `__loop__` Workflows Are Not Tracked in `LatestVersions`

`LoadAll` skips any `WorkflowDefinition` whose `Name` starts with `"__"` when building `LatestVersions`. These are internal synthetic definitions never started via a trigger and never subject to `ResolveVersionedName` unversioned lookup.

### 1.13 No Changes to `DeclarativeLoopOrchestrator` or `DeclarativePollerOrchestrator`

`DeclarativeLoopOrchestrator` calls `registry.Get(input.InnerWorkflowName)`. That name is already version-embedded because the loader generates it with the versioned `workflowContext`. `DeclarativePollerOrchestrator` does not look up workflows by name at all. Neither requires changes.

---

## 2. Changed Files

| File | Change |
|---|---|
| `src/DeclarativeDurableFunctions/Models/WorkflowDefinition.cs` | Add `Version`, `VersionedName` |
| `src/DeclarativeDurableFunctions/Models/StepDefinition.cs` | Add `WorkflowVersion` |
| `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionLoader.cs` | Parse `version:`, strip `-vN` suffix, versioned `workflowContext`, new return type, parse step `version:` |
| `src/DeclarativeDurableFunctions/Engine/IWorkflowDefinitionRegistry.cs` | Add `ResolveVersionedName` to public interface |
| `src/DeclarativeDurableFunctions/Engine/WorkflowDefinitionRegistry.cs` | New constructor, `ResolveVersionedName` implementation, updated `Get`/`TryGet` |
| `src/DeclarativeDurableFunctions/Extensions/ServiceCollectionExtensions.cs` | Destructure `LoadAll` tuple, pass both dicts to registry constructor |
| `src/DeclarativeDurableFunctions/Extensions/DynamicOrchestrationContextExtensions.cs` | Pass `internalRegistry` to `DynamicWorkflowRunner.RunAsync` |
| `src/DeclarativeDurableFunctions/Engine/DynamicWorkflowRunner.cs` | Add registry parameter to `RunAsync`, resolve versioned name in sub-orch and foreach-suborchestration dispatch |
| `src/DeclarativeDurableFunctions/Functions/GenericHttpTrigger.cs` | Inject registry, resolve versioned name before scheduling |
| `tests/DeclarativeDurableFunctions.Tests/Unit/WorkflowDefinitionRegistryTests.cs` | Update existing tests, add new versioning tests |

---

## 3. YAML Schema Changes

### 3.1 Top-Level Workflow Version

```yaml
workflow:
  name: Order Fulfillment    # optional; DisplayName only; unchanged
  version: 2                 # NEW: optional; positive integer; defaults to 1 if omitted
  steps:
    - ...
```

Rules:
- `version:` is optional. Omitting it is equivalent to `version: 1`.
- Must be a positive integer (`>= 1`). Any other value is a `WorkflowDefinitionException` at load time.
- Does not need to match the file name suffix, but they should agree by convention. The YAML value is the source of truth; the file name suffix is for human navigation.

File naming convention: `{BaseName}-v{N}.yaml`

| File name | Resulting base name | Registry key |
|---|---|---|
| `OrderFulfillment.yaml` | `OrderFulfillment` | `OrderFulfillment:1` (if `version:` absent) |
| `OrderFulfillment-v1.yaml` | `OrderFulfillment` | `OrderFulfillment:1` |
| `OrderFulfillment-v2.yaml` | `OrderFulfillment` | `OrderFulfillment:2` |
| `FulfillLineItem-v3.yaml` | `FulfillLineItem` | `FulfillLineItem:3` |

### 3.2 Sub-orchestration Step Version Pinning

```yaml
- name: RunSubWorkflow
  type: sub-orchestration
  workflow: OrderValidation
  version: 1                 # NEW: optional; positive integer; pins to exact version at dispatch time
  input: "{{input}}"
  output: validationResult
  instanceId: "{{input.orderId}}"
  condition: "{{...}}"
  retry:
    maxAttempts: 3
    firstRetryInterval: PT5S
```

Rules:
- `version:` is optional. If omitted, the sub-orchestration resolves to the current latest registered version at the moment the step first executes.
- If present, the versioned name `"{workflow}:{version}"` must exist in the registry at execution time; if not found, the orchestration fails with `WorkflowDefinitionException`.

### 3.3 Foreach Step Version Pinning

```yaml
- name: FulfillLineItems
  type: foreach
  source: "{{input.lineItems}}"
  workflow: FulfillLineItem
  version: 1                 # NEW: optional; same semantics as sub-orchestration version
  input:
    parent:
      orchestrationId: "{{orchestration.instanceId}}"
    data: "{{$item}}"
  instanceId: "{{$item.lineItemId}}"
  output: fulfillmentResults
```

Rules: identical to sub-orchestration `version:` pinning. Applies only when `workflow:` is set; silently ignored when only `activity:` is set.

---

## 4. C# Type Changes

### 4.1 `WorkflowDefinition` (Models/WorkflowDefinition.cs)

```csharp
sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;      // base name; file name with -vN stripped
    public string? DisplayName { get; init; }               // workflow.name in YAML; metadata only
    public int Version { get; init; } = 1;                 // NEW: workflow.version in YAML; default 1
    public string VersionedName => $"{Name}:{Version}";  // NEW: computed; registry key
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
}
```

`VersionedName` is a computed property. It is not stored separately and does not need to be serialized.

### 4.2 `StepDefinition` (Models/StepDefinition.cs)

Add one field in the `// Loop` section, immediately after the existing `LoopWorkflowName`:

```csharp
// Sub-orchestration / Foreach: explicit version pin; null = resolve to latest at dispatch time
public string? WorkflowVersion { get; init; }
```

This field is meaningful only when `Type` is `SubOrchestration`, or `Foreach` with `WorkflowName != null`. It is parsed from the step's `version:` YAML field and stored as a string for forward compatibility.

### 4.3 `IWorkflowDefinitionRegistry` (Engine/IWorkflowDefinitionRegistry.cs)

```csharp
public interface IWorkflowDefinitionRegistry
{
    IReadOnlyCollection<string> WorkflowNames { get; }   // all versioned keys registered
    string ResolveVersionedName(string workflowName);    // NEW: see §1.6 for rules
}

// Engine-internal only
interface IWorkflowDefinitionRegistryInternal : IWorkflowDefinitionRegistry
{
    WorkflowDefinition Get(string workflowName);         // accepts versioned or unversioned
    bool TryGet(string workflowName, out WorkflowDefinition? definition);
}
```

### 4.4 `WorkflowDefinitionRegistry` (Engine/WorkflowDefinitionRegistry.cs)

New constructor signature. Replace the existing primary constructor:

```csharp
sealed class WorkflowDefinitionRegistry(
    IReadOnlyDictionary<string, WorkflowDefinition> definitions,
    IReadOnlyDictionary<string, int> latestVersions) : IWorkflowDefinitionRegistryInternal
{
    // definitions key: versioned name, e.g. "OrderFulfillment:2" or "__loop__OrderFulfillment:2__StepName"
    // latestVersions key: base name, e.g. "OrderFulfillment"; value: highest version integer
}
```

---

## 5. Engine Implementation Guide

### 5.1 `WorkflowDefinitionLoader` Changes

#### New: `StripVersionSuffix` (private static method)

Strip a trailing `-v` followed by one or more digits from a file name (without extension):

```
private static string StripVersionSuffix(string fileName)
    Use Regex: Regex.Replace(fileName, @"-v\d+$", "")
    "OrderFulfillment-v2"  → "OrderFulfillment"
    "OrderFulfillment-v12" → "OrderFulfillment"
    "FulfillLineItem-v1"   → "FulfillLineItem"
    "OrderFulfillment"     → "OrderFulfillment"  (no suffix; unchanged)
```

#### Changed: `LoadAll` (return type and body)

```
public static (IReadOnlyDictionary<string, WorkflowDefinition> Definitions,
               IReadOnlyDictionary<string, int> LatestVersions) LoadAll(string directory)

var definitions    = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal)
var latestVersions = new Dictionary<string, int>(StringComparer.Ordinal)

foreach string file in Directory.EnumerateFiles(directory, "*.yaml"):
    string rawName  = Path.GetFileNameWithoutExtension(file)
    string baseName = StripVersionSuffix(rawName)        // strips -vN suffix
    string yaml     = File.ReadAllText(file)

    foreach var (k, v) in LoadFromYamlAll(yaml, baseName):
        definitions[k] = v
        // Skip internal synthetic loop workflows when tracking latest versions
        if !v.Name.StartsWith("__", StringComparison.Ordinal):
            if !latestVersions.TryGetValue(v.Name, out int current) || v.Version > current:
                latestVersions[v.Name] = v.Version

return (definitions, latestVersions)
```

#### Changed: `LoadFromYamlAll`

Change the final registration line to use `def.VersionedName` as the accumulator key instead of `workflowName`:

```
// was: accumulator[workflowName] = def;
accumulator[def.VersionedName] = def;    // e.g. "OrderFulfillment:2"
return accumulator;
```

Loop inner workflows are already in `accumulator` using their versioned-embedded key (set by `ParseStep`); no change needed for them.

#### Changed: `LoadFromYamlCore`

After reading `displayName`, parse `version:` and compute the versioned name. Then **pass the versioned name as `workflowContext`** to `ParseSteps`:

```
string? displayName = GetString(workflowNode, "name")

int version = GetInt(workflowNode, "version") ?? 1
if version < 1:
    throw new WorkflowDefinitionException(
        $"'workflow.version' must be a positive integer in workflow '{workflowName}'.", workflowName)

string versionedName = $"{workflowName}:{version}"

var stepsRaw = GetList(workflowNode, "steps")
    ?? throw new WorkflowDefinitionException(
        "'workflow.steps' is required and must be a sequence.", workflowName)

return new WorkflowDefinition
{
    Name        = workflowName,            // base name; unchanged
    DisplayName = displayName,
    Version     = version,                 // NEW
    Steps       = ParseSteps(stepsRaw, versionedName, accumulator)
    //                       ^^^^^^^^^^ was: workflowName
}
```

By passing `versionedName` as `workflowContext` to `ParseSteps`:
- Error messages become more informative (show versioned name)
- Loop inner workflows are named `"__loop__OrderFulfillment:2__StatusLoop"` automatically, with no other change to the loop case in `ParseStep`

#### Changed: `ParseStep` — parse `WorkflowVersion` for sub-orchestration and foreach

After the existing field reads (near where `stepWorkflow` is read), add:

```
string? stepWorkflowVersion = GetString(dict, "version")
```

Include it in the returned `StepDefinition`:

```
return new StepDefinition
{
    ...
    WorkflowName    = stepWorkflow,
    WorkflowVersion = stepWorkflowVersion,    // NEW
    ...
}
```

No validation is required on `stepWorkflowVersion` at parse time — an invalid version will fail at dispatch time with a `WorkflowDefinitionException` from the registry.

---

### 5.2 `WorkflowDefinitionRegistry` Implementation

#### `ResolveVersionedName`

```
public string ResolveVersionedName(string workflowName)
    if workflowName.Contains(':'):
        return workflowName   // already versioned or __loop__ name with embedded version

    if !_latestVersions.TryGetValue(workflowName, out int latest):
        throw new WorkflowDefinitionException(
            $"No workflow named '{workflowName}' is registered.", workflowName)

    return $"{workflowName}:{latest}"
```

#### `Get`

```
public WorkflowDefinition Get(string workflowName)
    string key = ResolveVersionedName(workflowName)
    if !_definitions.TryGetValue(key, out var def):
        throw new WorkflowDefinitionException($"Workflow '{key}' not found.", workflowName)
    return def
```

#### `TryGet`

```
public bool TryGet(string workflowName, out WorkflowDefinition? definition)
    if workflowName.Contains(':'):
        return _definitions.TryGetValue(workflowName, out definition)

    if !_latestVersions.TryGetValue(workflowName, out int latest):
        definition = null
        return false

    return _definitions.TryGetValue($"{workflowName}:{latest}", out definition)
```

#### `WorkflowNames`

```
public IReadOnlyCollection<string> WorkflowNames => _definitions.Keys.ToList().AsReadOnly()
```

Returns all keys including versioned names and `__loop__` inner names. Example:
`["OrderFulfillment:1", "OrderFulfillment:2", "__loop__OrderFulfillment:2__StatusLoop", "FulfillLineItem:1"]`

---

### 5.3 `GenericHttpTrigger` Changes

Change from static method to instance class with constructor injection. Remove `static` from `StartAsync`. Add primary constructor:

```csharp
public class GenericHttpTrigger(IWorkflowDefinitionRegistry registry)
{
    internal const string FunctionName = "StartWorkflow";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function(FunctionName)]
    public async Task<HttpResponseData> StartAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "workflows/{workflowName}")] HttpRequestData req,
        string workflowName,
        [DurableClient] DurableTaskClient client)
    {
        string versionedName = registry.ResolveVersionedName(workflowName);
        var input = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, JsonOptions);
        var envelope = new Dictionary<string, object?>
        {
            ["__workflow"] = versionedName,    // always store versioned name
            ["__input"]    = input
        };
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            GenericOrchestrator.FunctionName, envelope);
        var response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { instanceId }, JsonOptions));
        return response;
    }
}
```

`ResolveVersionedName` throws `WorkflowDefinitionException` when the workflow is not registered. This propagates as a 500. The current behavior for unknown workflow names is also a runtime error, so this is not a regression.

---

### 5.4 `DynamicWorkflowRunner` Changes

#### `RunAsync` — add registry parameter

```csharp
public static async Task<JsonElement> RunAsync(
    TaskOrchestrationContext context,
    WorkflowDefinition definition,
    WorkflowExecutionContext execCtx,
    IWorkflowDefinitionRegistryInternal registry)    // NEW
```

Pass `registry` down to every method that dispatches sub-orchestrations: `RunSubOrchestration` and `DispatchForeachSubOrch`. No other methods need it.

#### `RunSubOrchestration` — resolve versioned name before wrapping in envelope

```
// Resolve the versioned name before wrapping:
string versionedWorkflowName = step.WorkflowVersion != null
    ? $"{step.WorkflowName}:{step.WorkflowVersion}"      // explicit pin from YAML
    : registry.ResolveVersionedName(step.WorkflowName!)   // resolve to current latest

var result = await context.CallSubOrchestratorAsync<JsonElement>(
    DynamicOrchestrationContextExtensions.GenericSubOrchestrationFunctionName,
    WrapSubOrchInput(versionedWorkflowName, resolvedInput),    // was: step.WorkflowName!
    options)
```

Add `IWorkflowDefinitionRegistryInternal registry` as a parameter to `RunSubOrchestration`.

#### `DispatchForeachSubOrch` — resolve versioned name before wrapping in envelope

```
// Resolve the versioned name before wrapping:
string versionedWorkflowName = step.WorkflowVersion != null
    ? $"{step.WorkflowName}:{step.WorkflowVersion}"
    : registry.ResolveVersionedName(step.WorkflowName!)

return context.CallSubOrchestratorAsync<JsonElement>(
    DynamicOrchestrationContextExtensions.GenericSubOrchestrationFunctionName,
    WrapSubOrchInput(versionedWorkflowName, resolvedInput),    // was: step.WorkflowName!
    BuildSubOrchOptions(step, instanceId))
```

Add `IWorkflowDefinitionRegistryInternal registry` as a parameter to `DispatchForeachSubOrch`.

---

### 5.5 `DynamicOrchestrationContextExtensions` Changes

Pass `internalRegistry` to `DynamicWorkflowRunner.RunAsync` as the fourth argument:

```csharp
// was:
return DynamicWorkflowRunner.RunAsync(context, definition, execCtx);

// becomes:
return DynamicWorkflowRunner.RunAsync(context, definition, execCtx, internalRegistry);
```

No other changes to this file.

---

### 5.6 `ServiceCollectionExtensions` Changes

Destructure the tuple returned by `LoadAll` and pass both dicts to the registry:

```csharp
// was:
var definitions = WorkflowDefinitionLoader.LoadAll(directory);
var registry    = new WorkflowDefinitionRegistry(definitions);

// becomes:
var (definitions, latestVersions) = WorkflowDefinitionLoader.LoadAll(directory);
var registry = new WorkflowDefinitionRegistry(definitions, latestVersions);
```

No other changes to this file.

---

## 6. Test Changes

### 6.1 Updates to All Existing Tests in `WorkflowDefinitionRegistryTests`

Every test that constructs `WorkflowDefinitionRegistry` directly must pass both dictionaries.

Before:
```csharp
var def = new WorkflowDefinition { Name = "MyWorkflow", Steps = [] };
var registry = new WorkflowDefinitionRegistry(
    new Dictionary<string, WorkflowDefinition> { ["MyWorkflow"] = def });
```

After:
```csharp
var def = new WorkflowDefinition { Name = "MyWorkflow", Version = 1, Steps = [] };
var registry = new WorkflowDefinitionRegistry(
    definitions:    new Dictionary<string, WorkflowDefinition> { ["MyWorkflow:1"] = def },
    latestVersions: new Dictionary<string, int> { ["MyWorkflow"] = 1 });
```

Tests that call `registry.Get("MyWorkflow")` continue to work — unversioned lookup resolves to latest. Tests asserting on `registry.WorkflowNames` must expect versioned names (e.g., `"MyWorkflow:1"`).

### 6.2 New Tests for `WorkflowDefinitionRegistryTests`

| Test | What to verify |
|---|---|
| `LoadFromYaml_WithVersionField_ParsesVersion` | YAML with `version: 3` → `def.Version == 3`, `def.VersionedName == "MyWorkflow:3"` |
| `LoadFromYaml_WithoutVersionField_DefaultsToVersion1` | YAML without `version:` → `def.Version == 1`, `def.VersionedName == "MyWorkflow:1"` |
| `LoadFromYaml_VersionFieldLessThanOne_ThrowsWorkflowDefinitionException` | `version: 0` → `WorkflowDefinitionException` |
| `LoadFromYaml_SubOrchestrationStepWithVersion_ParsesWorkflowVersion` | Step with `version: 2` on sub-orchestration step → `step.WorkflowVersion == "2"` |
| `LoadFromYaml_SubOrchestrationStepWithoutVersion_WorkflowVersionIsNull` | Sub-orchestration step without `version:` → `step.WorkflowVersion == null` |
| `LoadFromYaml_ForeachStepWithVersion_ParsesWorkflowVersion` | Foreach step with `version: 1` → `step.WorkflowVersion == "1"` |
| `Registry_GetByVersionedName_ReturnsCorrectDefinition` | `registry.Get("MyWorkflow:2")` returns v2 definition |
| `Registry_GetByUnversionedName_ReturnsLatestVersion` | Two definitions registered (v1, v2); `registry.Get("MyWorkflow")` returns v2 |
| `Registry_GetUnknownWorkflow_ThrowsWorkflowDefinitionException` | `registry.Get("Unknown")` → `WorkflowDefinitionException` |
| `Registry_GetUnknownVersionedName_ThrowsWorkflowDefinitionException` | `registry.Get("MyWorkflow:99")` → `WorkflowDefinitionException` |
| `Registry_TryGetByVersionedName_ReturnsTrueAndDefinition` | `registry.TryGet("MyWorkflow:2", out var def)` → true, def is v2 |
| `Registry_TryGetByUnversionedName_ResolvesToLatest` | v1 and v2 registered; `TryGet("MyWorkflow", ...)` → true, returns v2 |
| `Registry_TryGetUnknownWorkflow_ReturnsFalse` | `TryGet("Unknown", ...)` → false, definition is null |
| `Registry_ResolveVersionedName_AlreadyVersioned_PassesThrough` | `ResolveVersionedName("MyWorkflow:2")` → `"MyWorkflow:2"` unchanged |
| `Registry_ResolveVersionedName_UnversionedName_ReturnsLatest` | latest=2 → `ResolveVersionedName("MyWorkflow")` → `"MyWorkflow:2"` |
| `Registry_ResolveVersionedName_LoopInnerNameWithEmbeddedColon_PassesThrough` | `ResolveVersionedName("__loop__MyWorkflow:2__StepName")` → unchanged (colon triggers passthrough) |
| `Registry_ResolveVersionedName_UnknownWorkflow_ThrowsWorkflowDefinitionException` | `ResolveVersionedName("Unknown")` → `WorkflowDefinitionException` |
| `Registry_WorkflowNames_ContainsVersionedNames` | `WorkflowNames` contains `"Alpha:1"`, `"Beta:2"`, not bare names |

### 6.3 New Runner Tests for `WorkflowRunnerTests`

These require a mock `IWorkflowDefinitionRegistryInternal` injected into `DynamicWorkflowRunner.RunAsync`.

| Test | What to verify |
|---|---|
| `SubOrchestration_WithExplicitVersion_UsesVersionedNameDirectly` | Step declares `WorkflowVersion = "1"`, registry has v2 as latest; `__workflow` in envelope is `"Sub:1"` — registry `ResolveVersionedName` is NOT called |
| `SubOrchestration_WithoutVersion_ResolvesToLatestViaRegistry` | Step has `WorkflowVersion = null`; registry resolves `"Sub"` → `"Sub:2"`; `__workflow` in envelope is `"Sub:2"` |
| `Foreach_SubOrch_WithExplicitVersion_UsesVersionedName` | Foreach step with `WorkflowVersion = "1"`; each iteration envelope contains `"Sub:1"` |
| `Foreach_SubOrch_WithoutVersion_ResolvesToLatest` | Foreach step with `WorkflowVersion = null`; envelope contains `"Sub:2"` |

---

## 7. Sections of `docs/spec.md` to Update

The following sections must be updated to reflect versioning. Update the spec in the same PR as the implementation, or in a dedicated doc-update phase.

### §1.10 Workflow Name Resolution

Old: `"The workflow name is the YAML filename without extension, case-preserved."`

New: `"The workflow name is the YAML filename without extension with any -vN suffix stripped, case-preserved. The registry key is '{name}:{version}' where version comes from the workflow.version: field in the YAML (default: 1)."`

### §5.1 Top-Level Structure

Add `version:` to the documented YAML schema block.

### §5.5 Sub-orchestration Step

Add `version:` field to the documented YAML schema block and its rules table.

### §5.6 Foreach Step

Add `version:` field to the documented YAML schema block and its rules table (applies only when `workflow:` is set).

### §7.2 Internal Model Types — `WorkflowDefinition`

Add `Version` and `VersionedName` to the documented type definition.

### §7.2 Internal Model Types — `StepDefinition`

Add `WorkflowVersion` to the documented type definition.

### §7.1 Public Types — `IWorkflowDefinitionRegistry`

Add `ResolveVersionedName` to the interface definition.
