using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Extensions;

public static class OrchestrationContextExtensions
{
    public static Task<JsonElement> RunWorkflowAsync(
        this TaskOrchestrationContext context,
        IWorkflowDefinitionRegistry registry)
    {
        if (registry is not IWorkflowDefinitionRegistryInternal internalRegistry)
            throw new InvalidOperationException(
                "Registry must be the framework-provided IWorkflowDefinitionRegistry implementation.");

        var workflowName = context.Name;
        if (!internalRegistry.TryGet(workflowName, out var definition) || definition is null)
            throw new WorkflowDefinitionException(
                $"No workflow definition registered for orchestration '{workflowName}'.", workflowName);

        var input = ResolveInput(context);
        var execCtx = new WorkflowExecutionContext(input, context);
        return WorkflowRunner.RunAsync(context, definition, execCtx);
    }

    private static JsonElement ResolveInput(TaskOrchestrationContext context)
    {
        try
        {
            return context.GetInput<JsonElement>();
        }
        catch
        {
            return JsonDocument.Parse("null").RootElement;
        }
    }
}
