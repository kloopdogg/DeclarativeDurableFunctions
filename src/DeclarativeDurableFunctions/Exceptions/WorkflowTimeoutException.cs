namespace DeclarativeDurableFunctions.Exceptions;

public class WorkflowTimeoutException(string stepName, string timeout)
    : Exception($"Step '{stepName}' timed out after {timeout}");
