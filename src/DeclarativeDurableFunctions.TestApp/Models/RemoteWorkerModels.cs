using System.Text.Json.Serialization;

namespace DeclarativeDurableFunctions.TestApp.Models;

public class RemoteWorkerResponse
{
    [JsonPropertyName("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
}

public class RemoteWorkerScrapingRequest
{
    [JsonPropertyName("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = string.Empty;
}