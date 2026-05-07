using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class WriteTelegramMessageActivity(ILogger<WriteTelegramMessageActivity> logger)
{
    [Function("WriteTelegramMessageActivity")]
    public object RunAsync([ActivityTrigger] JsonElement input)
    {
        string status = input.GetProperty("status").GetString()!;
        var data = input.GetProperty("data");
        
        logger.LogWarning("Writing Telegram message for status: '{Status}' and data: '{Data}'", status, data);

        //TODO: Have LLM use the input prompt and the status/schedule result to craft
        // a user-friendly message about the schedule availability and details.
        
        return new
        {
            telegramMessage = status.ToLower(System.Globalization.CultureInfo.CurrentCulture) is "succeeded" or "approved" or "success" or "completed"
                    ? $"Success! {data}"
                    : "Failed. Restart the workflow, if necessary."
        };
    }
}
