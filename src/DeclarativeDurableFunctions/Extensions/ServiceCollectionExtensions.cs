using DeclarativeDurableFunctions.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace DeclarativeDurableFunctions.Extensions;

public class WorkflowDefinitionRegistryOptions
{
    public string WorkflowsDirectory { get; set; } = "Workflows";
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeclarativeWorkflows(
        this IServiceCollection services,
        Action<WorkflowDefinitionRegistryOptions>? configure = null)
        => throw new NotImplementedException();
}
