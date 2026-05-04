using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class ScrapeSpaSiteActivity
{
    [Function("ScrapeSpaSiteActivity")]
    public object RunAsync(
        [ActivityTrigger] JsonElement input,
        FunctionContext context)
    {
        ILogger logger = context.GetLogger<ScrapeSpaSiteActivity>();
        string prompt = input.GetProperty("prompt").GetString()!;
        string url = input.GetProperty("url").GetString()!;
        string parentInstanceId = input.GetProperty("parent").GetProperty("orchestrationId").GetString()!;

        logger.LogWarning("Scraping spa site for prompt: {Prompt} and URL: {Url}", prompt, url);
        logger.LogWarning("ParentInstanceId: {ParentInstanceId}", parentInstanceId);

        return new
        {
            siteName = "USA Hockey Nationals Schedule 2026",
            status = "Schedule Found",
            gamesScheduled = new[]
            {
                new { date = "2026-01-01", teams = "Away vs MN Lakers" },
                new { date = "2026-01-02", teams = "Home vs ID Steelheads" },
                new { date = "2026-01-03", teams = "Home vs CO Eagles" },
            },
        };
    }
}
