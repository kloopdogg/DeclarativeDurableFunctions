using DeclarativeDurableFunctions.Models;

namespace DeclarativeDurableFunctions.Engine;

internal sealed class WorkflowDefinitionRegistry : IWorkflowDefinitionRegistryInternal
{
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _definitions;

    public WorkflowDefinitionRegistry(IReadOnlyDictionary<string, WorkflowDefinition> definitions)
    {
        _definitions = definitions;
    }

    public IReadOnlyCollection<string> WorkflowNames => _definitions.Keys.ToList().AsReadOnly();

    public WorkflowDefinition Get(string workflowName)
    {
        if (!_definitions.TryGetValue(workflowName, out var definition))
            throw new Exceptions.WorkflowDefinitionException($"Workflow '{workflowName}' not found.", workflowName);
        return definition;
    }

    public bool TryGet(string workflowName, out WorkflowDefinition? definition)
        => _definitions.TryGetValue(workflowName, out definition);
}
