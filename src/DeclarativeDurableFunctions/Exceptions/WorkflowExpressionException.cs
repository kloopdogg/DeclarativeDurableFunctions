namespace DeclarativeDurableFunctions.Exceptions;

public class WorkflowExpressionException(string expression, string reason, Exception? inner = null)
    : Exception($"Expression '{expression}' failed: {reason}", inner);
