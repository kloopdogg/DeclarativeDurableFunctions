namespace DeclarativeDurableFunctions.Exceptions;

public class WorkflowDefinitionException(string message, string? workflowName = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? WorkflowName { get; } = workflowName;
}
