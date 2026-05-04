using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.TestApp.Functions;

public class GenericSubOrchestrator(IWorkflowDefinitionRegistry registry)
{
    [Function(DynamicOrchestrationContextExtensions.GenericSubOrchestrationFunctionName)]
    public Task<JsonElement> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowDynamicAsync(registry);
}
