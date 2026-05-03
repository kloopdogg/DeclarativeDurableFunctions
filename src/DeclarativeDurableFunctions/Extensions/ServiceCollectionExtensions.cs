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
    {
        var options = new WorkflowDefinitionRegistryOptions();
        configure?.Invoke(options);

        var directory = Path.IsPathRooted(options.WorkflowsDirectory)
            ? options.WorkflowsDirectory
            : Path.Combine(AppContext.BaseDirectory, options.WorkflowsDirectory);

        var definitions = WorkflowDefinitionLoader.LoadAll(directory);
        var registry = new WorkflowDefinitionRegistry(definitions);
        services.AddSingleton<IWorkflowDefinitionRegistry>(registry);
        return services;
    }
}
