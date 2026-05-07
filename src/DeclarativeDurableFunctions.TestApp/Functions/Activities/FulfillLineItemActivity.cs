using DeclarativeDurableFunctions.Models;
using DeclarativeDurableFunctions.TestApp.Models;
using Microsoft.Azure.Functions.Worker;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class FulfillLineItemActivity
{
    [Function("FulfillLineItemActivity")]
    public static object RunAsync([ActivityTrigger] WorkflowInput<LineItem> input) => new
    {
        fulfillmentId = Guid.NewGuid().ToString(),
        lineItemId = input.Data.LineItemId,
        status = "fulfilled"
    };
}
