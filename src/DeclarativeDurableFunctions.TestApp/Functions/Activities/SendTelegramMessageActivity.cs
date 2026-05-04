using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class SendTelegramMessageActivity
{
    [Function("SendTelegramMessageActivity")]
    public object RunAsync([ActivityTrigger] string status, FunctionContext context)
    {
         ILogger logger = context.GetLogger(nameof(SendTelegramMessageActivity));
        logger.LogWarning("Sending Telegram message with status: {Status}", status);

        return new
        {
            confirmationId = Guid.NewGuid().ToString(),
            chatId = "123",
            status = status,
            sentAt = DateTime.UtcNow.ToString("O")
        };
    }
}
