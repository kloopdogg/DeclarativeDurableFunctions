using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using DeclarativeDurableFunctions.TestApp.Models;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class RemoteWorkerActivities(ILogger<RemoteWorkerActivities> logger,
    IAzureClientFactory<ServiceBusClient> serviceBusClientFactory)
{
    [Function("SendRemoteWorkerSpaScrapingRequestActivity")]
    public async Task SendRemoteWorkerSpaScrapingRequestActivity(
        [ActivityTrigger] RemoteWorkerScrapingRequest request,
        FunctionContext context)
    {
        string messageBody = JsonSerializer.Serialize(request);
        ServiceBusMessage message = new ServiceBusMessage(BinaryData.FromString(messageBody))
        {
            Subject = "playwright-task",
            CorrelationId = request.CorrelationId,
            ReplyTo = "ProjectOrchestrator",
            ContentType = "application/json",
        };
        message.ApplicationProperties["CorrelationId"] = request.CorrelationId;

        ServiceBusClient serviceBusClient = serviceBusClientFactory.CreateClient("ServiceBusSender");
        ServiceBusSender sender = serviceBusClient.CreateSender("agent-task-requests");
        await sender.SendMessageAsync(message, context.CancellationToken);

        logger.LogWarning("Sent message to remote worker with CorrelationId: '{CorrelationId}', Url: '{Url}', Instruction: '{Instruction}'", request.CorrelationId, request.Url, request.Instruction);
    }

    [Function("ReceiveRemoteWorkerResponseActivity")]
    public async Task ReceiveRemoteWorkerResponseActivity(
        [ServiceBusTrigger("agent-task-responses", "orchestrator-responses", Connection = "AltServiceBusConnection")] string message,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        RemoteWorkerResponse? payload = JsonSerializer.Deserialize<RemoteWorkerResponse>(message);

        if (payload == null)
        {
            logger.LogError("Failed to deserialize message from remote worker: '{Message}'", message);
            return;
        }

        string instanceId = payload.CorrelationId;

        logger.LogWarning("Received message from remote worker with CorrelationId: '{CorrelationId}', Status: '{Status}''", payload.CorrelationId, payload.Status);

        //correlationId is the instanceId used to raise the event
        await client.RaiseEventAsync(instanceId, "RemoteWorkCompleted", payload);
    }
}