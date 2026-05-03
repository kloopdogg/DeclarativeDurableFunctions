using System.Text.Json;
using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Engine;

internal static class WorkflowRunner
{
    public static Task RunAsync(
        TaskOrchestrationContext context,
        WorkflowDefinition definition,
        WorkflowExecutionContext execCtx)
        => ExecuteSteps(context, definition.Steps, execCtx);

    private static async Task ExecuteSteps(
        TaskOrchestrationContext context,
        IReadOnlyList<StepDefinition> steps,
        WorkflowExecutionContext execCtx)
    {
        foreach (var step in steps)
            await ExecuteStep(context, step, execCtx);
    }

    // outputNameOverride: used by parallel branches to store the result under the child's step name
    // rather than its output: field. Null means use step.Output as normal.
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

    // ---- SubOrchestration ----

    private static async Task RunSubOrchestration(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, execCtx);
        var instanceId = BuildInstanceId(context, step, execCtx);
        var options = BuildSubOrchOptions(step, instanceId);
        var result = await context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName!, resolvedInput, options);
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
            ? Stringify(ExpressionEvaluator.Evaluate(step.InstanceId, execCtx))
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
        var options = BuildActivityOptions(step);
        return context.CallActivityAsync<JsonElement>(step.ActivityName!, resolvedInput, options);
    }

    private static Task<JsonElement> DispatchForeachSubOrch(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext iterCtx)
    {
        var resolvedInput = ExpressionEvaluator.ResolveInputTemplate(step.Input, iterCtx);
        var instanceId = BuildInstanceId(context, step, iterCtx);
        var options = BuildSubOrchOptions(step, instanceId);
        return context.CallSubOrchestratorAsync<JsonElement>(step.WorkflowName!, resolvedInput, options);
    }

    // ---- Parallel ----

    private static async Task RunParallel(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx,
        string? outputNameOverride = null)
    {
        // Each branch gets a snapshot of the parent context so branches cannot observe
        // each other's in-flight outputs. The step name is the implicit output key.
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

        // on-timeout: continue → materialize explicit null so downstream references see null
        // rather than a missing-key error, whether this step ran sequentially or inside a parallel branch.
        var effectiveOutputOnTimeout = outputNameOverride ?? step.Output;
        if (effectiveOutputOnTimeout != null)
            execCtx.SetOutput(effectiveOutputOnTimeout, null);
    }

    // ---- Switch ----

    private static async Task RunSwitch(
        TaskOrchestrationContext context,
        StepDefinition step,
        WorkflowExecutionContext execCtx)
    {
        var onValue = ExpressionEvaluator.Evaluate(step.SwitchOn!, execCtx);
        var key = Stringify(onValue);

        if (!step.Cases.TryGetValue(key, out var caseSteps))
            step.Cases.TryGetValue("default", out caseSteps);

        if (caseSteps != null)
            await ExecuteSteps(context, caseSteps, execCtx);
    }

    // ---- Helpers ----

    private static string Stringify(object? value) => value switch
    {
        null => "",
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
        JsonElement je when je.ValueKind == JsonValueKind.Null   => "",
        JsonElement je => je.ToString(),
        _ => value.ToString() ?? ""
    };
}
