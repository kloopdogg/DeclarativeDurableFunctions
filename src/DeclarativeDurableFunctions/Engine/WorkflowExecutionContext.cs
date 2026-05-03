using Microsoft.DurableTask;
using System.Text.Json;

namespace DeclarativeDurableFunctions.Engine;

internal sealed class WorkflowExecutionContext
{
    private readonly Dictionary<string, object?> _outputs;

    public WorkflowExecutionContext(JsonElement input, TaskOrchestrationContext orchestrationContext)
    {
        Input = input;
        InstanceId = orchestrationContext.InstanceId;
        ParentInstanceId = orchestrationContext.Parent?.InstanceId;
        _outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        IterationItem = null;
        IterationIndex = null;
    }

    private WorkflowExecutionContext(
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
        _outputs = new Dictionary<string, object?>(outputs, StringComparer.Ordinal);
        IterationItem = iterationItem;
        IterationIndex = iterationIndex;
    }

    private WorkflowExecutionContext(
        JsonElement input,
        string instanceId,
        string? parentInstanceId,
        Dictionary<string, object?> outputs)
    {
        Input = input;
        InstanceId = instanceId;
        ParentInstanceId = parentInstanceId;
        _outputs = new Dictionary<string, object?>(outputs, StringComparer.Ordinal);
        IterationItem = null;
        IterationIndex = null;
    }

    public JsonElement Input { get; }
    public string InstanceId { get; }
    public string? ParentInstanceId { get; }

    public JsonElement? IterationItem { get; }
    public int? IterationIndex { get; }

    public void SetOutput(string name, object? value)
    {
        if (value is JsonElement element)
            _outputs[name] = element.Clone();
        else
            _outputs[name] = value;
    }

    public object? GetOutput(string name) => _outputs[name];

    public bool HasOutput(string name) => _outputs.ContainsKey(name);

    public WorkflowExecutionContext CreateIterationScope(JsonElement item, int index)
        => new WorkflowExecutionContext(Input, InstanceId, ParentInstanceId, _outputs, item, index);

    public WorkflowExecutionContext CreateParallelBranchScope()
        => new WorkflowExecutionContext(Input, InstanceId, ParentInstanceId, _outputs);
}
