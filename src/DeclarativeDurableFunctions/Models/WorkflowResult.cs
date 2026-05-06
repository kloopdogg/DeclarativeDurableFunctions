using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeclarativeDurableFunctions.Models;

public sealed class WorkflowResult
{
    [JsonPropertyName("workflowStatus")]
    public string WorkflowStatus { get; init; } = "Succeeded";

    [JsonPropertyName("output")]
    public JsonElement? Output { get; init; }

    [JsonPropertyName("error")]
    public WorkflowError? Error { get; init; }
}

public sealed class WorkflowError
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "Error";

    [JsonPropertyName("step")]
    public string? Step { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("timeout")]
    public string? Timeout { get; init; }
}
