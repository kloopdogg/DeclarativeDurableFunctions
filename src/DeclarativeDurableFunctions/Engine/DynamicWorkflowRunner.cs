using System.Text.Json;
using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Extensions;
using DeclarativeDurableFunctions.Models;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Engine;

/// <summary>
/// Identical to WorkflowRunner except sub-orchestration steps are always dispatched to the
/// single GenericSubOrchestration function, with the workflow name passed in the input envelope.
/// Use via context.RunWorkflowDynamicAsync(registry) — no named stub per workflow required.
/// </summary>
internal static class DynamicWorkflowRunner
{
    public static async Task<JsonElement> RunAsync(
        TaskOrchestrationContext context,
        WorkflowDefinition definition,
        WorkflowExecutionContext execCtx)
    {
        await ExecuteSteps(context, definition.Steps, execCtx);
        return JsonSerializer.SerializeToElement(execCtx.Outputs);
    }

    private static async Task ExecuteSteps(
        TaskOrchestrationContext context,
        IReadOnlyList<StepDefinition> steps,
        WorkflowExecutionContext execCtx)
    {
        foreach (var step in steps)
            await ExecuteStep(context, step, execCtx);
    }

    private static async Task ExecuteStep(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        if (step.Condition != null && !ExpressionEvaluator.EvaluateBool(step.Condition, execCtx))
            return;

        switch (step.Type)
        {
            case StepType.Activity:         await RunActivity(context, step, execCtx, outputNameOverride); break;
            case StepType.SubOrchestration: await RunSubOrchestration(context, step, execCtx, outputNameOverride); break;
            case StepType.Foreach:          await RunForeach(context, step, execCtx, outputNameOverride); break;
            case StepType.Parallel:         await RunParallel(context, step, execCtx, outputNameOverride); break;
            case StepType.WaitForEvent:     await RunWaitForEvent(context, step, execCtx, outputNameOverride); break;
            case StepType.Switch:           await RunSwitch(context, step, execCtx); break;
            case StepType.Poll:             await RunPoll(context, step, execCtx, outputNameOverride); break;
        }
    }

    // ---- Activity ----

    private static async Task RunActivity(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx);
        var options = BuildActivityOptions(step);
        var result = await context.CallActivityAsync<JsonElement>(step.ActivityName!, resolvedInput, options);
        var effectiveOutput = outputNameOverride ?? step.Output;
        if (effectiveOutput != null)
            execCtx.SetOutput(effectiveOutput, result);
    }

    private static TaskOptions? BuildActivityOptions(StepDefinition step)
        => step.Retry != null
            ? TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy())
            : null;

    // ---- SubOrchestration (routes through GenericSubOrchestration) ----

    private static async Task RunSubOrchestration(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx);
        var instanceId = BuildInstanceId(context, step, execCtx);
        var options = BuildSubOrchOptions(step, instanceId);
        var result = await context.CallSubOrchestratorAsync<JsonElement>(
            DynamicOrchestrationContextExtensions.GenericSubOrchestrationFunctionName,
            WrapSubOrchInput(step.WorkflowName!, resolvedInput),
            options);
        var effectiveOutput = outputNameOverride ?? step.Output;
        if (effectiveOutput != null)
            execCtx.SetOutput(effectiveOutput, result);
    }

    private static SubOrchestrationOptions BuildSubOrchOptions(StepDefinition step, string instanceId)
    {
        if (step.Retry != null)
            return TaskOptions.FromRetryPolicy(step.Retry.ToSdkRetryPolicy()).WithInstanceId(instanceId);
        return new SubOrchestrationOptions(retry: null, instanceId: instanceId);
    }

    private static string BuildInstanceId(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx)
    {
        var suffix = step.InstanceId != null
            ? ExpressionEvaluator.Stringify(ExpressionEvaluator.Evaluate(step.InstanceId, execCtx))
            : context.NewGuid().ToString();
        return $"{context.InstanceId}:{step.Name}:{suffix}";
    }

    // ---- Foreach ----

    private static async Task RunForeach(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var sourceVal = step.Source != null ? ExpressionEvaluator.Evaluate(step.Source, execCtx) : null;
        if (sourceVal is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            throw new WorkflowDefinitionException(
                $"foreach step '{step.Name}' source did not resolve to a JSON array.");

        var items = arr.EnumerateArray().ToList();
        var tasks = new Task<JsonElement>[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var iterCtx = execCtx.CreateIterationScope(items[i], i);
            tasks[i] = step.WorkflowName != null
                ? DispatchForeachSubOrch(context, step, iterCtx)
                : DispatchForeachActivity(context, step, iterCtx);
        }

        var results = await Task.WhenAll(tasks);
        var effectiveOutput = outputNameOverride ?? step.Output;
        if (effectiveOutput != null)
            execCtx.SetOutput(effectiveOutput, JsonSerializer.SerializeToElement(results));
    }

    private static Task<JsonElement> DispatchForeachActivity(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext iterCtx)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, iterCtx);
        return context.CallActivityAsync<JsonElement>(step.ActivityName!, resolvedInput, BuildActivityOptions(step));
    }

    private static Task<JsonElement> DispatchForeachSubOrch(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext iterCtx)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, iterCtx);
        var instanceId = BuildInstanceId(context, step, iterCtx);
        return context.CallSubOrchestratorAsync<JsonElement>(
            DynamicOrchestrationContextExtensions.GenericSubOrchestrationFunctionName,
            WrapSubOrchInput(step.WorkflowName!, resolvedInput),
            BuildSubOrchOptions(step, instanceId));
    }

    // ---- Parallel ----

    private static async Task RunParallel(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var branchScopes = step.Steps.Select(_ => execCtx.CreateParallelBranchScope()).ToList();
        var tasks = step.Steps
            .Select((child, i) => ExecuteStep(context, child, branchScopes[i], outputNameOverride: child.Name))
            .ToArray();
        await Task.WhenAll(tasks);

        var effectiveOutput = outputNameOverride ?? step.Output;
        if (effectiveOutput != null)
        {
            var aggregate = new Dictionary<string, object?>();
            for (var i = 0; i < step.Steps.Count; i++)
            {
                var child = step.Steps[i];
                if (child.Name != null)
                    aggregate[child.Name] = branchScopes[i].HasOutput(child.Name)
                        ? branchScopes[i].GetOutput(child.Name)
                        : null;
            }
            execCtx.SetOutput(effectiveOutput, JsonSerializer.SerializeToElement(aggregate));
        }
    }

    // ---- WaitForEvent ----

    private static async Task RunWaitForEvent(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var eventTask = context.WaitForExternalEvent<JsonElement>(step.EventName!);

        if (string.IsNullOrEmpty(step.Timeout))
        {
            var payload = await eventTask;
            var effectiveOutput = outputNameOverride ?? step.Output;
            if (effectiveOutput != null)
                execCtx.SetOutput(effectiveOutput, payload);
            return;
        }

        var timeoutSpan = Iso8601DurationParser.Parse(step.Timeout);
        using var cts = new CancellationTokenSource();
        var timerTask = context.CreateTimer(context.CurrentUtcDateTime.Add(timeoutSpan), cts.Token);
        var winner = await Task.WhenAny(eventTask, timerTask);

        if (winner == eventTask)
        {
            cts.Cancel();
            var payload = await eventTask;
            var effectiveOutput = outputNameOverride ?? step.Output;
            if (effectiveOutput != null)
                execCtx.SetOutput(effectiveOutput, payload);
            return;
        }

        if (step.OnTimeout == "fail")
            throw new WorkflowTimeoutException(step.Name ?? "(unnamed)", step.Timeout);

        var effectiveOutputOnTimeout = outputNameOverride ?? step.Output;
        if (effectiveOutputOnTimeout != null)
            execCtx.SetOutput(effectiveOutputOnTimeout, null);
    }

    // ---- Poll ----

    private static async Task RunPoll(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx);
        var activityInputJson = JsonSerializer.SerializeToElement(resolvedInput);

        var pollerInput = new PollerInput
        {
            ActivityName    = step.ActivityName!,
            ActivityInput   = activityInputJson,
            OutputName      = step.Output!,
            UntilExpression = step.Until!,
            Delay           = step.Delay!,
            Timeout         = step.Timeout,
            OnTimeout       = step.OnTimeout,
            StartedAt       = context.CurrentUtcDateTime
        };

        var instanceId = $"{context.InstanceId}:{step.Name ?? step.ActivityName}:poller";
        var options = new SubOrchestrationOptions(retry: null, instanceId: instanceId);
        var result = await context.CallSubOrchestratorAsync<JsonElement>(
            DeclarativePollerOrchestrator.FunctionName, pollerInput, options);

        var effectiveOutput = outputNameOverride ?? step.Output;
        if (effectiveOutput != null)
            execCtx.SetOutput(effectiveOutput, result);
    }

    // ---- Switch ----

    private static async Task RunSwitch(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx)
    {
        var onValue = ExpressionEvaluator.Evaluate(step.SwitchOn!, execCtx);
        var key = ExpressionEvaluator.Stringify(onValue);

        if (!step.Cases.TryGetValue(key, out var caseSteps))
            step.Cases.TryGetValue("default", out caseSteps);

        if (caseSteps != null)
            await ExecuteSteps(context, caseSteps, execCtx);
    }

    // ---- Helpers ----

    private static Dictionary<string, object?> WrapSubOrchInput(string workflowName, object? input)
        => new() { ["__workflow"] = workflowName, ["__input"] = input };

}
