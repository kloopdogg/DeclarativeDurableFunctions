using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;

namespace DeclarativeDurableFunctions.Functions;

public class GenericHttpTrigger
{
    internal const string FunctionName = "StartWorkflow";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function(FunctionName)]
    public static async Task<HttpResponseData> StartAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "workflows/{workflowName}")] HttpRequestData req,
        string workflowName,
        [DurableClient] DurableTaskClient client)
    {
        var input = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, JsonOptions);
        var envelope = new Dictionary<string, object?>
        {
            ["__workflow"] = workflowName,
            ["__input"] = input
        };

        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(GenericOrchestrator.FunctionName, envelope);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { instanceId }, JsonOptions));
        return response;
    }
}
