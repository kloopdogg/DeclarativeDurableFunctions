using Microsoft.DurableTask;
using System.Text.Json;

namespace DeclarativeDurableFunctions.Engine;

internal sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(JsonElement input, TaskOrchestrationContext orchestrationContext)
        => throw new NotImplementedException();

    public JsonElement Input { get; }
    public string InstanceId { get; } = string.Empty;
    public string? ParentInstanceId { get; }

    public void SetOutput(string name, object? value) => throw new NotImplementedException();
    public object? GetOutput(string name) => throw new NotImplementedException();
    public bool HasOutput(string name) => throw new NotImplementedException();

    public WorkflowExecutionContext CreateIterationScope(JsonElement item, int index)
        => throw new NotImplementedException();
}
