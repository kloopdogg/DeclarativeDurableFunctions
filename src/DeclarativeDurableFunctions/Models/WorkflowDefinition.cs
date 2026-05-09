namespace DeclarativeDurableFunctions.Models;

sealed class WorkflowDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public int Version { get; init; } = 1;
    public string VersionedName => $"{Name}:{Version}";
    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];
}
