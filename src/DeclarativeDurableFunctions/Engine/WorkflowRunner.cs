using DeclarativeDurableFunctions.Models;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Engine;

internal sealed class WorkflowRunner
{
    public Task RunAsync(
        TaskOrchestrationContext context,
        WorkflowDefinition definition,
        WorkflowExecutionContext execCtx)
        => throw new NotImplementedException();
}
