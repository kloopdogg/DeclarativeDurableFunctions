// using System.Net;
// using System.Text.Json;
// using DeclarativeDurableFunctions.TestApp.Models;
// using Microsoft.Azure.Functions.Worker;
// using Microsoft.Azure.Functions.Worker.Http;
// using Microsoft.DurableTask.Client;

// namespace DeclarativeDurableFunctions.TestApp.Functions;

// public class OrderHttpTrigger
// {
//     private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

//     [Function("StartOrderFulfillment")]
//     public async Task<HttpResponseData> StartAsync(
//         [HttpTrigger(AuthorizationLevel.Function, "post", Route = "orders")] HttpRequestData req,
//         [DurableClient] DurableTaskClient client)
//     {
//         var order = await JsonSerializer.DeserializeAsync<Order>(req.Body, JsonOptions);
//         if (order is null)
//         {
//             var bad = req.CreateResponse(HttpStatusCode.BadRequest);
//             await bad.WriteStringAsync("Request body must be a valid Order JSON object.");
//             return bad;
//         }

//         order.CorrelationId ??= Guid.NewGuid().ToString();

//         var instanceId = await client.ScheduleNewOrchestrationInstanceAsync("OrderFulfillment", order);

//         var response = req.CreateResponse(HttpStatusCode.Accepted);
//         response.Headers.Add("Content-Type", "application/json");
//         await response.WriteStringAsync(JsonSerializer.Serialize(new { instanceId }, JsonOptions));
//         return response;
//     }
// }
