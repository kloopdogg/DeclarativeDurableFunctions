using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Extensions;
using DeclarativeDurableFunctions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Functions;

public class GenericOrchestrator(IWorkflowDefinitionRegistry registry)
{
    public const string FunctionName = "GenericOrchestrator";

    [Function(FunctionName)]
    public async Task<WorkflowResult> RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        try
        {
            var output = await context.RunWorkflowDynamicAsync(registry);
            return new WorkflowResult { WorkflowStatus = "Succeeded", Output = output };
        }
        catch (WorkflowTimeoutException ex)
        {
            return new WorkflowResult
            {
                WorkflowStatus = "Failed",
                Error = new WorkflowError
                {
                    Type = "Timeout",
                    Step = ex.StepName,
                    Message = ex.Message,
                    Timeout = ex.Timeout
                }
            };
        }
        catch (Exception ex)
        {
            return new WorkflowResult
            {
                WorkflowStatus = "Failed",
                Error = new WorkflowError { Type = "Error", Message = ex.Message }
            };
        }
    }
}
