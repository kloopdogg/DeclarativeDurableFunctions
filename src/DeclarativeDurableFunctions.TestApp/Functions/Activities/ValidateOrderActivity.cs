using DeclarativeDurableFunctions.TestApp.Models;
using Microsoft.Azure.Functions.Worker;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class ValidateOrderActivity
{
    [Function("ValidateOrderActivity")]
    public object RunAsync([ActivityTrigger] Order order, FunctionContext context)
        => new
        {
            isValid = !string.IsNullOrEmpty(order.OrderId) && order.LineItems.Count > 0,
            reason = order.LineItems.Count == 0 ? "Order has no line items." : null
        };
}
