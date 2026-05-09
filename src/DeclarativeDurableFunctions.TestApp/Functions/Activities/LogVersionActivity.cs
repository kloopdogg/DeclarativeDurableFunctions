using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public partial class LogVersionActivity(ILogger<LogVersionActivity> logger)
{
    [Function("LogVersionActivityV1")]
    public async Task<object> LogVersionActivityV1([ActivityTrigger] string orchestrationId, int version = 1)
    {
        LogVersionMessage(orchestrationId, version);

        await Task.Delay(1000); // Simulate some work

        return new { logged = true };
    }

    [Function("LogVersionActivityV2")]
    public async Task<object> LogVersionActivityV2([ActivityTrigger] string orchestrationId, int version = 2)
    {
        LogVersionMessage(orchestrationId, version);

        await Task.Delay(1000); // Simulate some work

        return new { logged = true };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "LogVersionActivity for v{Version} orchestration from orchestrationId: {OrchestrationId}")]
    private partial void LogVersionMessage(string orchestrationId, int version);
}
