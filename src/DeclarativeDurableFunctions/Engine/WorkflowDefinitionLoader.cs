using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;
using YamlDotNet.Serialization;

namespace DeclarativeDurableFunctions.Engine;

static class WorkflowDefinitionLoader
{
    static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static IReadOnlyDictionary<string, WorkflowDefinition> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new WorkflowDefinitionException($"Workflows directory '{directory}' does not exist.");
        }

        var definitions = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory, "*.yaml"))
        {
            string workflowName = Path.GetFileNameWithoutExtension(file);
            string yaml = File.ReadAllText(file);
            foreach (var (k, v) in LoadFromYamlAll(yaml, workflowName))
            {
                definitions[k] = v;
            }
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

    static WorkflowDefinition LoadFromYamlCore(
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

        string? displayName = GetString(workflowNode, "name");
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

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    static IReadOnlyList<StepDefinition> ParseSteps(
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
        List<object> stepsRaw, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        var steps = new List<StepDefinition>(stepsRaw.Count);
        foreach (object raw in stepsRaw)
        {
            if (raw is not Dictionary<object, object> stepDict)
            {
                throw new WorkflowDefinitionException(
                    $"A step in workflow '{workflowContext}' is not a mapping.", workflowContext);
            }

            steps.Add(ParseStep(stepDict, workflowContext, accumulator));
        }
        return steps.AsReadOnly();
    }

    static StepDefinition ParseStep(
        Dictionary<object, object> dict, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        string? name = GetString(dict, "name");
        string? typeStr = GetString(dict, "type");
        string? activityName = GetString(dict, "activity");
        string? stepWorkflow = GetString(dict, "workflow");
        object? input = GetRaw(dict, "input");
        string? output = GetString(dict, "output");
        string? condition = GetString(dict, "condition");
        string? instanceId = GetString(dict, "instanceId") ?? GetString(dict, "instance-id");
        string? source = GetString(dict, "source");

        var retryDict = GetDict(dict, "retry");
        var retry = retryDict != null ? ParseRetryPolicy(retryDict, workflowContext, name) : null;

        var stepType = InferStepType(typeStr, activityName, stepWorkflow, workflowContext, name);

        IReadOnlyList<StepDefinition> subSteps = [];
        string? eventName = null;
        string? timeout = null;
        string onTimeout = "fail";
        string? switchOn = null;
        IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> cases =
            new Dictionary<string, IReadOnlyList<StepDefinition>>();
        string? until = null;
        string? delay = null;
        string? breakWhen = null;
        string? loopWorkflowName = null;

#pragma warning disable IDE0010 // Add missing cases
        switch (stepType)
        {
            case StepType.Foreach:
                if (source == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) is missing required 'source' field.", workflowContext);
                }

                if (activityName != null && stepWorkflow != null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) must have exactly one of 'activity' or 'workflow', not both.",
                        workflowContext);
                }

                if (activityName == null && stepWorkflow == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (foreach) must have 'activity' or 'workflow'.", workflowContext);
                }

                break;

            case StepType.Parallel:
                var parallelStepsRaw = GetList(dict, "steps") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (parallel) is missing required 'steps' sequence.", workflowContext);
                subSteps = ParseSteps(parallelStepsRaw, workflowContext, accumulator);
                foreach (var child in subSteps)
                {
                    if (child.Output != null)
                    {
                        throw new WorkflowDefinitionException(
                            $"Step '{child.Name}' inside parallel block '{name}': 'output:' is not valid on parallel child steps. " +
                            $"Branch results are keyed by step name and collected via the block's own 'output:' field.",
                            workflowContext);
                    }
                }
                break;

            case StepType.WaitForEvent:
                eventName = GetString(dict, "event") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (wait-for-event) is missing required 'event' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout is not "fail" and not "continue")
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                }

                break;

            case StepType.Switch:
                switchOn = GetString(dict, "on") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (switch) is missing required 'on' field.", workflowContext);

                var casesRaw = GetDict(dict, "cases") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (switch) is missing required 'cases' field.", workflowContext);
                cases = ParseCases(casesRaw, workflowContext, accumulator);
                break;

            case StepType.Poll:
                if (activityName == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'activity' field.", workflowContext);
                }

                if (output == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'output' field. " +
                        "The output name is used in the 'until' expression to reference the activity result.",
                        workflowContext);
                }

                until = GetString(dict, "until") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'until' field.", workflowContext);

                delay = GetString(dict, "delay") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (poll) is missing required 'delay' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout is not "fail" and not "continue")
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                }

                break;

            case StepType.TriggerAndWait:
                if (activityName == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (trigger-and-wait) is missing required 'activity' field.", workflowContext);
                }

                eventName = GetString(dict, "event") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (trigger-and-wait) is missing required 'event' field.", workflowContext);
                timeout = GetString(dict, "timeout");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout is not "fail" and not "continue")
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                }

                break;

            case StepType.Loop:
                if (name == null)
                {
                    throw new WorkflowDefinitionException(
                        "Loop steps must have a 'name' field.", workflowContext);
                }

                if (output == null)
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'output' field.", workflowContext);
                }

                breakWhen = GetString(dict, "break-when") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'break-when' field.", workflowContext);

                delay = GetString(dict, "delay") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'delay' field.", workflowContext);
                timeout = GetString(dict, "max-duration");
                onTimeout = GetString(dict, "on-timeout") ?? "fail";
                if (onTimeout is not "fail" and not "continue")
                {
                    throw new WorkflowDefinitionException(
                        $"Step '{name}': 'on-timeout' must be 'fail' or 'continue', got '{onTimeout}'.",
                        workflowContext);
                }

                var loopStepsRaw = GetList(dict, "steps") ?? throw new WorkflowDefinitionException(
                        $"Step '{name}' (loop) is missing required 'steps' sequence.", workflowContext);
                subSteps = ParseSteps(loopStepsRaw, workflowContext, accumulator);
                loopWorkflowName = $"__loop__{workflowContext}__{name}";
                accumulator[loopWorkflowName] = new WorkflowDefinition
                {
                    Name = loopWorkflowName,
                    Steps = subSteps
                };
                break;
            case StepType.Activity:
                break;
            case StepType.SubOrchestration:
                break;
        }
#pragma warning restore IDE0010 // Add missing cases

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

    static StepType InferStepType(
        string? typeStr, string? activityName, string? stepWorkflow,
        string workflowContext, string? stepName)
    {
        // foreach can be combined with activity or workflow
        if (typeStr == "foreach")
        {
            return StepType.Foreach;
        }

        // activity field present → Activity (no type required)
        if (activityName != null && (typeStr == null || typeStr == "activity"))
        {
            return StepType.Activity;
        }

        // workflow field present, no type → SubOrchestration
        return stepWorkflow != null && typeStr == null
            ? StepType.SubOrchestration
            : typeStr switch
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

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    static IReadOnlyDictionary<string, IReadOnlyList<StepDefinition>> ParseCases(
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
        Dictionary<object, object> casesDict, string workflowContext, Dictionary<string, WorkflowDefinition> accumulator)
    {
        var result = new Dictionary<string, IReadOnlyList<StepDefinition>>(StringComparer.Ordinal);
        foreach (var (key, value) in casesDict)
        {
            string caseKey = key.ToString()!;
            if (value is not List<object> stepsRaw)
            {
                throw new WorkflowDefinitionException(
                    $"Switch case '{caseKey}' must be a sequence of steps.", workflowContext);
            }

            result[caseKey] = ParseSteps(stepsRaw, workflowContext, accumulator);
        }
        return result;
    }

    static AppRetryPolicy ParseRetryPolicy(
        Dictionary<object, object> dict, string workflowContext, string? stepName)
    {
        int maxAttempts = GetInt(dict, "maxAttempts") ?? GetInt(dict, "max-attempts") ?? 1;
        return maxAttempts < 1
            ? throw new WorkflowDefinitionException(
                $"retry.maxAttempts must be >= 1 on step '{stepName}'.", workflowContext)
            : new AppRetryPolicy
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

    static Dictionary<object, object>? GetDict(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out object? val) ? val as Dictionary<object, object> : null;

    static List<object>? GetList(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out object? val) ? val as List<object> : null;

    static string? GetString(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out object? val) ? val?.ToString() : null;

    static object? GetRaw(Dictionary<object, object> dict, string key)
        => dict.TryGetValue(key, out object? val) ? val : null;

    static int? GetInt(Dictionary<object, object> dict, string key) => !dict.TryGetValue(key, out object? val)
            ? null
            : val switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out int r) => r,
                _ => null
            };

    static double? GetDouble(Dictionary<object, object> dict, string key) => !dict.TryGetValue(key, out object? val)
            ? null
            : val switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double r) => r,
                _ => null
            };
}
