using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using DeclarativeDurableFunctions.TestApp.Models;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class SendConfirmationEmailActivity
{
    [Function("SendConfirmationEmailActivity")]
    public object RunAsync([ActivityTrigger] SendEmailRequest sendEmailRequest, FunctionContext context)
    {
        ILogger logger = context.GetLogger(nameof(SendConfirmationEmailActivity));
        logger.LogWarning("Sending confirmation email to: '{EmailToAddress}' with subject: '{EmailSubject}'", sendEmailRequest.CustomerEmail, sendEmailRequest.Subject);

        //TODO: Send email

        return new
        {
            confirmationId = Guid.NewGuid().ToString(),
            sentTo = sendEmailRequest.CustomerEmail,
            sentAt = DateTime.UtcNow.ToString("O"),
            subject = sendEmailRequest.Subject,
        };
    }
}
