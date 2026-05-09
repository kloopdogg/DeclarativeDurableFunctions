using Microsoft.DurableTask;
using System.Text.Json;

namespace DeclarativeDurableFunctions.Engine;

sealed class WorkflowExecutionContext
{
    readonly Dictionary<string, object?> outputs;

    public WorkflowExecutionContext(JsonElement input, TaskOrchestrationContext orchestrationContext)
    {
        Input = input;
        InstanceId = orchestrationContext.InstanceId;
        ParentInstanceId = orchestrationContext.Parent?.InstanceId;
        outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        IterationItem = null;
        IterationIndex = null;
    }

    WorkflowExecutionContext(
        JsonElement input,
        string instanceId,
        string? parentInstanceId,
        Dictionary<string, object?> outputs,
        JsonElement iterationItem,
        int iterationIndex)
    {
        Input = input;
        InstanceId = instanceId;
        ParentInstanceId = parentInstanceId;
        this.outputs = new Dictionary<string, object?>(outputs, StringComparer.Ordinal);
        IterationItem = iterationItem;
        IterationIndex = iterationIndex;
    }

    WorkflowExecutionContext(
        JsonElement input,
        string instanceId,
        string? parentInstanceId,
        Dictionary<string, object?> outputs)
    {
        Input = input;
        InstanceId = instanceId;
        ParentInstanceId = parentInstanceId;
        this.outputs = new Dictionary<string, object?>(outputs, StringComparer.Ordinal);
        IterationItem = null;
        IterationIndex = null;
    }

    public JsonElement Input { get; }
    public string InstanceId { get; }
    public string? ParentInstanceId { get; }

    public JsonElement? IterationItem { get; }
    public int? IterationIndex { get; }

    public void SetOutput(string name, object? value) => outputs[name] = value is JsonElement element ? element.Clone() : value;

    public object? GetOutput(string name) => outputs[name];

    public bool HasOutput(string name) => outputs.ContainsKey(name);

    public IReadOnlyDictionary<string, object?> Outputs => outputs;

    public WorkflowExecutionContext CreateIterationScope(JsonElement item, int index)
        => new(Input, InstanceId, ParentInstanceId, outputs, item, index);

    public WorkflowExecutionContext CreateParallelBranchScope()
        => new(Input, InstanceId, ParentInstanceId, outputs);
}
