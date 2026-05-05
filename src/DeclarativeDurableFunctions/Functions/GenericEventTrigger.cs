using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace DeclarativeDurableFunctions.Functions;

public class GenericEventTrigger
{
    [Function("EventTrigger")]
    public async Task<HttpResponseData> EventTriggerAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{instanceId}/{eventName}")] HttpRequestData req,
        string instanceId,
        string eventName,
        [DurableClient] DurableTaskClient client)
    {
        JsonElement? body = req.Body.Length > 0
            ? await JsonSerializer.DeserializeAsync<JsonElement>(req.Body)
            : null;
        await client.RaiseEventAsync(instanceId, eventName, body);
        return req.CreateResponse(HttpStatusCode.Accepted);
    }
}
