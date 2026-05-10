using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeclarativeDurableFunctions.Extensions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((hostContext, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.Configure<LoggerFilterOptions>(options =>
        {
            var defaultRule = options.Rules.FirstOrDefault(rule =>
                rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (defaultRule is not null)
                options.Rules.Remove(defaultRule);
        });
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
