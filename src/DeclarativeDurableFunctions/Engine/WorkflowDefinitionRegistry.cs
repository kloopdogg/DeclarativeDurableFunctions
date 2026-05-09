using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;

namespace DeclarativeDurableFunctions.Engine;

sealed class WorkflowDefinitionRegistry(
    IReadOnlyDictionary<string, WorkflowDefinition> definitions,
    IReadOnlyDictionary<string, int> latestVersions) : IWorkflowDefinitionRegistryInternal
{
    public IReadOnlyCollection<string> WorkflowNames => definitions.Keys.ToList().AsReadOnly();

    public string ResolveVersionedName(string workflowName) => workflowName.Contains(':')
            ? workflowName
            : !latestVersions.TryGetValue(workflowName, out int latest)
            ? throw new WorkflowDefinitionException(
                $"No workflow named '{workflowName}' is registered.", workflowName)
            : $"{workflowName}:{latest}";

    public WorkflowDefinition Get(string workflowName)
    {
        string key = ResolveVersionedName(workflowName);
        return !definitions.TryGetValue(key, out var def)
            ? throw new WorkflowDefinitionException($"Workflow '{key}' not found.", workflowName)
            : def;
    }

    public bool TryGet(string workflowName, out WorkflowDefinition? definition)
    {
        if (workflowName.Contains(':'))
        {
            return definitions.TryGetValue(workflowName, out definition);
        }

        if (!latestVersions.TryGetValue(workflowName, out int latest))
        {
            definition = null;
            return false;
        }

        return definitions.TryGetValue($"{workflowName}:{latest}", out definition);
    }
}
