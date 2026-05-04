using System.Text.Json;
using Microsoft.Azure.Functions.Worker;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class UpdateLedgerActivity
{
    [Function("UpdateLedgerActivity")]
    public object RunAsync(
        [ActivityTrigger] JsonElement fulfillmentResults,
        FunctionContext context)
        => new
        {
            ledgerId = Guid.NewGuid().ToString(),
            itemCount = fulfillmentResults.ValueKind == JsonValueKind.Array
                ? fulfillmentResults.GetArrayLength()
                : 0,
            status = "updated"
        };
}
