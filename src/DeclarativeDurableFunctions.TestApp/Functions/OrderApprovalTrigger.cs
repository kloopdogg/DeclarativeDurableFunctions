using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace DeclarativeDurableFunctions.TestApp.Functions;

public class OrderApprovalTrigger
{
    [Function("ApproveOrder")]
    public async Task<HttpResponseData> ApproveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/{instanceId}/approve")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
        await client.RaiseEventAsync(instanceId, "OrderApproved", body);
        return req.CreateResponse(HttpStatusCode.Accepted);
    }
}
