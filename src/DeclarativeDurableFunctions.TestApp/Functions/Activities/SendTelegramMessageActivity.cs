using DeclarativeDurableFunctions.TestApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class SendTelegramMessageActivity
{
    [Function("SendTelegramMessageActivity")]
    public object RunAsync([ActivityTrigger] string formattedMessage, FunctionContext context)
    {
        ILogger logger = context.GetLogger(nameof(SendTelegramMessageActivity));
        logger.LogWarning("Sending Telegram message: {Message}", formattedMessage);

        //TODO: Send message and return real confirmation details from Telegram API response

        return new
        {
            confirmationId = Guid.NewGuid().ToString(),
            chatId = "123",
            message = formattedMessage,
            sentAt = DateTime.UtcNow.ToString("O")
        };
    }
}
