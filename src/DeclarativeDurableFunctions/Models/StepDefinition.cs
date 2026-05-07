namespace DeclarativeDurableFunctions.Models;

sealed class StepDefinition
{
    public string? Name { get; init; }
    public StepType Type { get; init; }
    public string? ActivityName { get; init; }
    public string? WorkflowName { get; init; }
    public object? Input { get; init; }
    public string? Output { get; init; }
    public string? Condition { get; init; }
    public AppRetryPolicy? Retry { get; init; }

    public string? Source { get; init; }
    public string? InstanceId { get; init; }

    public IReadOnlyList<StepDefinition> Steps { get; init; } = [];

    public string? EventName { get; init; }
    public string? Timeout { get; init; }
    public string OnTimeout { get; init; } = "fail";

    public string? SwitchOn { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> Cases { get; init; }
        = new Dictionary<string, IReadOnlyList<StepDefinition>>();

    // Poll
    public string? Until { get; init; }
    public string? Delay { get; init; }

    // Loop
    public string? BreakWhen { get; init; }
    public string? LoopWorkflowName { get; init; }
}
