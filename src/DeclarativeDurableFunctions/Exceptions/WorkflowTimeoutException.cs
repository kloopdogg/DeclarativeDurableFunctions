namespace DeclarativeDurableFunctions.Exceptions;

public class WorkflowTimeoutException(string stepName, string timeout)
    : Exception($"Step '{stepName}' timed out after {timeout}")
{
    public string StepName { get; } = stepName;
    public string Timeout { get; } = timeout;
}
