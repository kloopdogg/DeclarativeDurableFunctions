using Microsoft.Azure.Functions.Worker;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class SendConfirmationEmailActivity
{
    [Function("SendConfirmationEmailActivity")]
    public object RunAsync([ActivityTrigger] string customerEmail, FunctionContext context)
        => new
        {
            confirmationId = Guid.NewGuid().ToString(),
            sentTo = customerEmail,
            sentAt = DateTime.UtcNow.ToString("O")
        };
}
