using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;
using YamlDotNet.Serialization;

namespace DeclarativeDurableFunctions.Engine;

internal static class WorkflowDefinitionLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static IReadOnlyDictionary<string, WorkflowDefinition> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
            throw new WorkflowDefinitionException($"Workflows directory '{directory}' does not exist.");

        var definitions = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml"))
        {
            var workflowName = Path.GetFileNameWithoutExtension(file);
            var yaml = File.ReadAllText(file);
            foreach (var (k, v) in LoadFromYamlAll(yaml, workflowName))
                definitions[k] = v;
        }
        return definitions;
    }

    public static WorkflowDefinition LoadFromYaml(string yaml, string workflowName)
    {
        var accumulator = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        return LoadFromYamlCore(yaml, workflowName, accumulator);
    }

    // Returns the top-level workflow and any inner loop workflows registered during parse.
    internal static IReadOnlyDictionary<string, WorkflowDefinition> LoadFromYamlAll(string yaml, string workflowName)
    {
        var accumulator = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        var def = LoadFromYamlCore(yaml, workflowName, accumulator);
        accumulator[workflowName] = def;
        return accumulator;
    }

    private static WorkflowDefinition LoadFromYamlCore(
        string yaml, string workflowName, Dictionary<string, WorkflowDefinition> accumulator)
    {
        Dictionary<object, object> root;
        try
        {
            root = Deserializer.Deserialize<Dictionary<object, object>>(yaml)
                ?? throw new WorkflowDefinitionException("YAML document is empty.", workflowName);
        }
        catch (WorkflowDefinitionException) { throw; }
        catch (Exception ex)
        {
            throw new WorkflowDefinitionException(
                $"Failed to parse YAML for workflow '{workflowName}': {ex.Message}", workflowName, ex);
        }

        var workflowNode = GetDict(root, "workflow")
            ?? throw new WorkflowDefinitionException("Missing 'workflow' key.", workflowName);

        var displayName = GetString(workflowNode, "name");
        var stepsRaw = GetList(workflowNode, "steps")
            ?? throw new WorkflowDefinitionException(
                "'workflow.steps' is required and must be a sequence.", workflowName);

        return new WorkflowDefinition
        {
            Name = workflowName,
            DisplayName = displayName,
            Steps = ParseSteps(stepsRaw, workflowName, accumulator)
        };
    }

    private static IReadOnlyList<StepDefinition> ParseSteps(
        List<object> stepsRaw, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        var steps = new List<StepDefinition>(stepsRaw.Count);
        foreach (var raw in stepsRaw)
        {
            if (raw is not Dictionary<object, object> stepDict)
                throw new WorkflowDefinitionException(
                    $"A step in workflow '{workflowContext}' is not a mapping.", workflowContext);
            steps.Add(ParseStep(stepDict, workflowContext, accumulator));
        }
        return steps.AsReadOnly();
    }

    private static StepDefinition ParseStep(
        Dictionary<object, object> dict, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        var name = GetString(dict, "name");
        var typeStr = GetString(dict, "type");
        var activityName = GetString(dict, "activity");
        var stepWorkflow = GetString(dict, "workflow");
        var input = GetRaw(dict, "input");
        var output = GetString(dict, "output");
        var condition = GetString(dict, "condition");
        var instanceId = GetString(dict, "instanceId") ?? GetString(dict, "instance-id");
        var source = GetString(dict, "source");

        var retryDict = GetDict(dict, "retry");
        var retry = retryDict != null ? ParseRetryPolicy(retryDict, workflowContext, name) : null;

        var stepType = InferStepType(typeStr, activityName, stepWorkflow, workflowContext, name);

        IReadOnlyList<StepDefinition> subSteps = [];
        string? eventName = null;
        string? timeout = null;
        var onTimeout = "fail";
        string? switchOn = null;
        IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> cases =
            new Dictionary<string, IReadOnlyList<StepDefinition>>();
        string? until = null;
        string? delay = null;
        string? breakWhen = null;
        string? loopWorkflowName = null;

        switch (stepType)
        {
            case StepType.Foreach:
                if (source == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) is missing required 'source' field.", workflowContext);
                if (activityName != null && stepWorkflow != null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) must have exactly one of 'activity' or 'workflow', not both.",
                        workflowContext);
                if (activityName == null && stepWorkflow == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) must have 'activity' or 'workflow'.", workflowContext);
                break;

            case StepType.Parallel:
                var parallelStepsRaw = GetList(dict, "steps");
                if (parallelStepsRaw == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (parallel) is missing required 'steps' sequence.", workflowContext);
                subSteps = ParseSteps(parallelStepsRaw, workflowContext, accumulator);
                foreach (var child in subSteps)
                {
                    if (child.Output != null)
                        throw new WorkflowDefinitionException(
                            $"Step '{child.Name}' inside parallel block '{name}': 'output:' is not valid on parallel child steps. " +
                            $"Branch results are keyed by step name and collected via the block's own 'output:' field.",
                            workflowContext);
                }
                break;

            case StepType.WaitForEvent:
                eventName = GetString(dict, "event");
                if (eventName == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (wait-for-event) is missing required 'event' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout != "fail" && onTimeout != "continue")
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                break;

            case StepType.Switch:
                switchOn = GetString(dict, "on");
                if (switchOn == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (switch) is missing required 'on' field.", workflowContext);
                var casesRaw = GetDict(dict, "cases");
                if (casesRaw == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (switch) is missing required 'cases' field.", workflowContext);
                cases = ParseCases(casesRaw, workflowContext, accumulator);
                break;

            case StepType.Poll:
                if (activityName == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'activity' field.", workflowContext);
                if (output == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'output' field. " +
                        "The output name is used in the 'until' expression to reference the activity result.",
                        workflowContext);
                until = GetString(dict, "until");
                if (until == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'until' field.", workflowContext);
                delay = GetString(dict, "delay");
                if (delay == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'delay' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout != "fail" && onTimeout != "continue")
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                break;

            case StepType.TriggerAndWait:
                if (activityName == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (trigger-and-wait) is missing required 'activity' field.", workflowContext);
                eventName = GetString(dict, "event") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (trigger-and-wait) is missing required 'event' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout != "fail" && onTimeout != "continue")
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                break;

            case StepType.Loop:
                if (name == null)
                    throw new WorkflowDefinitionException(
                        "Loop steps must have a 'name' field.", workflowContext);
                if (output == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'output' field.", workflowContext);
                breakWhen = GetString(dict, "break-when");
                if (breakWhen == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'break-when' field.", workflowContext);
                delay = GetString(dict, "delay");
                if (delay == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'delay' field.", workflowContext);
                timeout = GetString(dict, "max-duration");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout != "fail" && onTimeout != "continue")
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                var loopStepsRaw = GetList(dict, "steps");
                if (loopStepsRaw == null)
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'steps' sequence.", workflowContext);
                subSteps = ParseSteps(loopStepsRaw, workflowContext, accumulator);
                loopWorkflowName = $"__loop__{workflowContext}__{name}";
                accumulator[loopWorkflowName] = new WorkflowDefinition
                {
                    Name = loopWorkflowName,
                    Steps = subSteps
                };
                break;
        }

        return new StepDefinition
        {
            Name = name,
            Type = stepType,
            ActivityName = activityName,
            WorkflowName = stepWorkflow,
            Input = input,
            Output = output,
            Condition = condition,
            InstanceId = instanceId,
            Source = source,
            Retry = retry,
            Steps = subSteps,
            EventName = eventName,
            Timeout = timeout,
            OnTimeout = onTimeout,
            SwitchOn = switchOn,
            Cases = cases,
            Until = until,
            Delay = delay,
            BreakWhen = breakWhen,
            LoopWorkflowName = loopWorkflowName
        };
    }

    private static StepType InferStepType(
        string? typeStr, string? activityName, string? stepWorkflow,
        string workflowContext, string? stepName)
    {
        // foreach can be combined with activity or workflow
        if (typeStr == "foreach")
            return StepType.Foreach;

        // activity field present → Activity (no type required)
        if (activityName != null && (typeStr == null || typeStr == "activity"))
            return StepType.Activity;

        // workflow field present, no type → SubOrchestration
        if (stepWorkflow != null && typeStr == null)
            return StepType.SubOrchestration;

        return typeStr switch
        {
            "activity"         => StepType.Activity,
            "sub-orchestration"=> StepType.SubOrchestration,
            "parallel"         => StepType.Parallel,
            "wait-for-event"   => StepType.WaitForEvent,
            "switch"           => StepType.Switch,
            "poll"             => StepType.Poll,
            "trigger-and-wait" => StepType.TriggerAndWait,
            "loop"             => StepType.Loop,
            null               => throw new WorkflowDefinitionException(
                                    $"Cannot infer step type for step '{stepName}': no 'activity', 'workflow', or 'type' field.",
                                    workflowContext),
            _                  => throw new WorkflowDefinitionException(
                                    $"Unknown step type '{typeStr}' on step '{stepName}'.", workflowContext)
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> ParseCases(
        Dictionary<object, object> casesDict, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        var result = new Dictionary<string, IReadOnlyList<StepDefinition>>(StringComparer.Ordinal);
        foreach (var (key, value) in casesDict)
        {
            var caseKey = key.ToString()!;
            if (value is not List<object> stepsRaw)
                throw new WorkflowDefinitionException(
                    $"Switch case '{caseKey}' must be a sequence of steps.", workflowContext);
            result[caseKey] = ParseSteps(stepsRaw, workflowContext, accumulator);
        }
        return result;
    }

    private static AppRetryPolicy ParseRetryPolicy(
        Dictionary<object, object> dict, string workflowContext, string? stepName)
    {
        var maxAttempts = GetInt(dict, "maxAttempts") ?? GetInt(dict, "max-attempts") ?? 1;
        if (maxAttempts < 1)
            throw new WorkflowDefinitionException(
                $"retry.maxAttempts must be >= 1 on step '{stepName}'.", workflowContext);

        return new AppRetryPolicy
        {
            MaxAttempts = maxAttempts,
            FirstRetryInterval = GetString(dict, "firstRetryInterval")
                               ?? GetString(dict, "first-retry-interval") ?? "PT1S",
            MaxRetryInterval = GetString(dict, "maxRetryInterval")
                             ?? GetString(dict, "max-retry-interval"),
            BackoffCoefficient = GetDouble(dict, "backoffCoefficient")
                               ?? GetDouble(dict, "backoff-coefficient") ?? 1.0
        };
    }

    // ---- Dictionary helpers ----

    private static Dictionary<object, object>? GetDict(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out var val) ? val as Dictionary<object, object> : null;

    private static List<object>? GetList(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out var val) ? val as List<object> : null;

    private static string? GetString(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static object? GetRaw(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out var val) ? val : null;

    private static int? GetInt(Dictionary<object, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        return val switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var r) => r,
            _ => null
        };
    }

    private static double? GetDouble(Dictionary<object, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        return val switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var r) => r,
            _ => null
        };
    }
}
