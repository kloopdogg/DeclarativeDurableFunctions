using System.Text.Json;
using DeclarativeDurableFunctions.Exceptions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Engine;

sealed class DeclarativePollerOrchestrator
{
    internal const string FunctionName = "DeclarativeWorkflowPoller";

    [Function(FunctionName)]
    public async Task<JsonElement> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        // In a durable orchestration function, we need the replay safe logger:
        var logger = context.CreateReplaySafeLogger(nameof(DeclarativePollerOrchestrator));

        // We use the BeginScope method on the logger to decorate all logs with the OrchestrationInstanceId
        using(logger.BeginScope(new Dictionary<string, object>{ ["OrchestrationInstanceId"] = context.InstanceId }))        
        {
            var input = context.GetInput<PollerInput>()!;

            var result = await context.CallActivityAsync<JsonElement>(
                input.ActivityName, input.ActivityInput);

            var miniCtx = new WorkflowExecutionContext(default, context);
            miniCtx.SetOutput(input.OutputName, result);

            if (ExpressionEvaluator.EvaluateBool(input.UntilExpression, miniCtx))
            {
                return result;
            }

            if (input.Timeout != null)
            {
                var elapsed = context.CurrentUtcDateTime - input.StartedAt;
                if (elapsed >= Iso8601DurationParser.Parse(input.Timeout))
                {
                    return input.OnTimeout == "fail" ? throw new WorkflowTimeoutException(input.ActivityName, input.Timeout) : result;
                }
            }

            await context.CreateTimer(
                context.CurrentUtcDateTime.Add(Iso8601DurationParser.Parse(input.Delay)),
                CancellationToken.None);

            context.ContinueAsNew(input);
            return result;
        }
    }
}
