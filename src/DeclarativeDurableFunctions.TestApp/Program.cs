using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Hosting;
using DeclarativeDurableFunctions.Extensions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((hostContext, services) =>
    {
        _ = services.AddDeclarativeWorkflows();
        services.AddAzureClients(clientBuilder =>
        {
            _ = clientBuilder.AddServiceBusClient(hostContext.Configuration["ServiceBusConnection"])
            .ConfigureOptions(options =>
            {
                options.RetryOptions.Delay = TimeSpan.FromSeconds(1);
                options.RetryOptions.MaxDelay = TimeSpan.FromSeconds(30);
                options.RetryOptions.MaxRetries = 5;
            })
            .WithName("ServiceBusSender");

            _ = clientBuilder.AddBlobServiceClient("AzureWebJobsStorage");
        });
    })
    .Build();

await host.RunAsync();
