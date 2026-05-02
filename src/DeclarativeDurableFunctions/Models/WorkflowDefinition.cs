namespace DeclarativeDurableFunctions.Models;

internal sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
}
