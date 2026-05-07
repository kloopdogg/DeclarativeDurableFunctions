using DeclarativeDurableFunctions.Models;

namespace DeclarativeDurableFunctions.Engine;

sealed class WorkflowDefinitionRegistry(IReadOnlyDictionary<string, WorkflowDefinition> definitions) : IWorkflowDefinitionRegistryInternal
{
    readonly IReadOnlyDictionary<string, WorkflowDefinition> definitions = definitions;

    public IReadOnlyCollection<string> WorkflowNames => definitions.Keys.ToList().AsReadOnly();

    public WorkflowDefinition Get(string workflowName) => !definitions.TryGetValue(workflowName, out var definition)
            ? throw new Exceptions.WorkflowDefinitionException($"Workflow '{workflowName}' not found.", workflowName)
            : definition;

    public bool TryGet(string workflowName, out WorkflowDefinition? definition)
        => definitions.TryGetValue(workflowName, out definition);
}
