using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Extensions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Functions;

public class GenericOrchestrator(IWorkflowDefinitionRegistry registry)
{
    public const string FunctionName = "GenericOrchestrator";

    [Function(FunctionName)]
    public Task<JsonElement> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowDynamicAsync(registry);
}
