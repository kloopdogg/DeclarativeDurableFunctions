using System.Text.Json;

namespace DeclarativeDurableFunctions.Engine;

sealed class LoopInput
{
    public string InnerWorkflowName { get; init; } = string.Empty;
    public string OutputName { get; init; } = string.Empty;
    public string BreakWhenExpression { get; init; } = string.Empty;
    public string Delay { get; init; } = string.Empty;
    public string? MaxDuration { get; init; }
    public string OnTimeout { get; init; } = "fail";
    public DateTimeOffset StartedAt { get; init; }
    public Dictionary<string, JsonElement> PreviousOutputs { get; init; } = [];
    public JsonElement ParentInput { get; init; }
}
