using DeclarativeDurableFunctions.Models;

namespace DeclarativeDurableFunctions.Engine;

internal static class WorkflowDefinitionLoader
{
    public static IReadOnlyDictionary<string, WorkflowDefinition> LoadAll(string directory)
        => throw new NotImplementedException();

    public static WorkflowDefinition LoadFromYaml(string yaml, string workflowName)
        => throw new NotImplementedException();
}
