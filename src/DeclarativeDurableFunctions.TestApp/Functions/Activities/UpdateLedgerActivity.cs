using System.Text.Json;
using Microsoft.Azure.Functions.Worker;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class UpdateLedgerActivity
{
    [Function("UpdateLedgerActivity")]
    public static object RunAsync([ActivityTrigger] JsonElement fulfillmentResults)
        => new
        {
            ledgerId = Guid.NewGuid().ToString(),
            itemCount = fulfillmentResults.ValueKind == JsonValueKind.Array
                ? fulfillmentResults.GetArrayLength()
                : 0,
            status = "updated"
        };
}
