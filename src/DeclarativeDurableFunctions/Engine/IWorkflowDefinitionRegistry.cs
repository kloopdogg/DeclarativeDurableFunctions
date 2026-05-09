using DeclarativeDurableFunctions.Models;

namespace DeclarativeDurableFunctions.Engine;

/// <summary>
/// Provides access to registered workflow definitions. Inject this into orchestrator classes
/// and pass it to <c>context.RunWorkflowAsync(registry)</c>.
/// </summary>
public interface IWorkflowDefinitionRegistry
{
    IReadOnlyCollection<string> WorkflowNames { get; }
    string ResolveVersionedName(string workflowName);
}

// Internal extension used by the engine — consumers never see WorkflowDefinition directly.
interface IWorkflowDefinitionRegistryInternal : IWorkflowDefinitionRegistry
{
    WorkflowDefinition Get(string workflowName);
    bool TryGet(string workflowName, out WorkflowDefinition? definition);
}
