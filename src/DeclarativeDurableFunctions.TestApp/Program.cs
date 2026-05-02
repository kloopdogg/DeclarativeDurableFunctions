using DeclarativeDurableFunctions.Extensions;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddDeclarativeWorkflows();
    })
    .Build();

await host.RunAsync();
