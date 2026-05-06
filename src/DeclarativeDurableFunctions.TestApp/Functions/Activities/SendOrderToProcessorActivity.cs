using DeclarativeDurableFunctions.TestApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.ServiceBus;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class SendOrderToProcessorActivity
{
    [Function("SendOrderToProcessorActivity")]
    [ServiceBusOutput("sample-queue", Connection = "ServiceBusConnection")]
    public object RunAsync([ActivityTrigger] RemoteProcessingRequest remoteProcessingRequest, FunctionContext context)
    {
        ILogger logger = context.GetLogger(nameof(SendOrderToProcessorActivity));
        logger.LogWarning("Sending order to processor: {OrderId}", remoteProcessingRequest.OrderId);

        // Send message to ASB queue with order details and callback info for when processing is complete
        return new
        {
            orderId = remoteProcessingRequest.OrderId,
            callbackInstanceId = remoteProcessingRequest.CallbackInstanceId,
        };
    }
}
