using System.Text.Json;
using DeclarativeDurableFunctions.Exceptions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Engine;

sealed class DeclarativeLoopOrchestrator(IWorkflowDefinitionRegistry registry)
{
    internal const string FunctionName = "DeclarativeWorkflowLoop";

    readonly IWorkflowDefinitionRegistryInternal registry =
        registry as IWorkflowDefinitionRegistryInternal
        ?? throw new InvalidOperationException(
            "Registry must be the framework-provided IWorkflowDefinitionRegistry implementation.");

    [Function(FunctionName)]
    public async Task<JsonElement> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<LoopInput>()!;

        // Clone all JsonElements immediately — the input buffer may be recycled across awaits.
        var parentInput = input.ParentInput.Clone();
        var innerDef = registry.Get(input.InnerWorkflowName);

        var execCtx = new WorkflowExecutionContext(parentInput, context);
        foreach (var (key, value) in input.PreviousOutputs)
        {
            execCtx.SetOutput(key, value.Clone());
        }

        _ = await DynamicWorkflowRunner.RunAsync(context, innerDef, execCtx);

        var currentOutput = GetOutput(execCtx, input.OutputName);

        if (ExpressionEvaluator.EvaluateBool(input.BreakWhenExpression, execCtx))
        {
            return currentOutput;
        }

        if (input.MaxDuration != null)
        {
            var elapsed = context.CurrentUtcDateTime - input.StartedAt;
            if (elapsed >= Iso8601DurationParser.Parse(input.MaxDuration))
            {
                return input.OnTimeout == "fail" ? throw new WorkflowTimeoutException(input.InnerWorkflowName, input.MaxDuration) : currentOutput;
            }
        }

        await context.CreateTimer(
            context.CurrentUtcDateTime.Add(Iso8601DurationParser.Parse(input.Delay)),
            CancellationToken.None);

        var nextOutputs = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in execCtx.Outputs)
        {
            nextOutputs[key] = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
        }

        context.ContinueAsNew(new LoopInput
        {
            InnerWorkflowName = input.InnerWorkflowName,
            OutputName = input.OutputName,
            BreakWhenExpression = input.BreakWhenExpression,
            Delay = input.Delay,
            MaxDuration = input.MaxDuration,
            OnTimeout = input.OnTimeout,
            StartedAt = input.StartedAt,
            PreviousOutputs = nextOutputs,
            ParentInput = parentInput
        });

        // Return the current output — ContinueAsNew discards this but it must be a valid JsonElement.
        return currentOutput;
    }

    static JsonElement GetOutput(WorkflowExecutionContext execCtx, string outputName)
        => execCtx.HasOutput(outputName) && execCtx.GetOutput(outputName) is JsonElement je
            ? je
            : JsonDocument.Parse("null").RootElement.Clone();
}
