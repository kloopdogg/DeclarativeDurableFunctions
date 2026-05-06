using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Extensions.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeclarativeDurableFunctions.Extensions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((hostContext, services) =>
    {
        services.AddDeclarativeWorkflows();
        services.AddAzureClients(clientBuilder =>
        {
            clientBuilder.AddServiceBusClient(hostContext.Configuration["ServiceBusConnection"]!)
            .ConfigureOptions(options =>
            {
                options.RetryOptions.Delay = TimeSpan.FromSeconds(1);
                options.RetryOptions.MaxDelay = TimeSpan.FromSeconds(30);
                options.RetryOptions.MaxRetries = 5;
            })
            .WithName("ServiceBusSender");

            clientBuilder.AddBlobServiceClient("AzureWebJobsStorage");
        });
    })
    .Build();

await host.RunAsync();
