using DeclarativeDurableFunctions.Engine;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Extensions;

public static class OrchestrationContextExtensions
{
    public static Task RunWorkflowAsync(
        this TaskOrchestrationContext context,
        IWorkflowDefinitionRegistry registry)
        => throw new NotImplementedException();
}
