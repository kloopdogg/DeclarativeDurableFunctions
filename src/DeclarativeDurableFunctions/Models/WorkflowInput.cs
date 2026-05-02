namespace DeclarativeDurableFunctions.Models;

public class WorkflowInput<TData>
{
    public WorkflowMetadata Parent { get; set; } = default!;
    public TData Data { get; set; } = default!;
}

public class WorkflowMetadata
{
    public string OrchestrationId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string? WorkflowName { get; set; }
}
