using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Extensions;

public static class DynamicOrchestrationContextExtensions
{
    public const string GenericSubOrchestrationFunctionName = "GenericSubOrchestration";

    /// <summary>
    /// Dynamic variant of RunWorkflowAsync. Sub-orchestration steps are routed through a single
    /// GenericSubOrchestration function rather than requiring a named stub per workflow.
    /// The workflow name is passed in the input envelope under the "__workflow" key.
    /// Top-level calls (no envelope) fall back to context.Name for the workflow lookup.
    /// </summary>
    public static Task<JsonElement> RunWorkflowDynamicAsync(
        this TaskOrchestrationContext context,
        IWorkflowDefinitionRegistry registry)
    {
        if (registry is not IWorkflowDefinitionRegistryInternal internalRegistry)
        {
            throw new InvalidOperationException(
                "Registry must be the framework-provided IWorkflowDefinitionRegistry implementation.");
        }

        var rawInput = ResolveInput(context);
        string workflowName;
        JsonElement actualInput;

        if (rawInput.ValueKind == JsonValueKind.Object &&
            rawInput.TryGetProperty("__workflow", out var wfProp) &&
            wfProp.ValueKind == JsonValueKind.String)
        {
            workflowName = wfProp.GetString()!;
            actualInput = rawInput.TryGetProperty("__input", out var inputProp)
                ? inputProp
                : JsonDocument.Parse("null").RootElement;
        }
        else
        {
            workflowName = context.Name;
            actualInput = rawInput;
        }

        if (!internalRegistry.TryGet(workflowName, out var definition) || definition is null)
        {
            throw new WorkflowDefinitionException(
                $"No workflow definition registered for orchestration '{workflowName}'.", workflowName);
        }

        var execCtx = new WorkflowExecutionContext(actualInput, context);
        return DynamicWorkflowRunner.RunAsync(context, definition, execCtx);
    }

    static JsonElement ResolveInput(TaskOrchestrationContext context)
    {
        try { return context.GetInput<JsonElement>(); }
        catch { return JsonDocument.Parse("null").RootElement; }
    }
}
