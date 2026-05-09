using System.Text.Json;

namespace DeclarativeDurableFunctions.Engine;

sealed class PollerInput
{
    public string ActivityName { get; init; } = string.Empty;
    public JsonElement? ActivityInput { get; init; }
    public string OutputName { get; init; } = string.Empty;
    public string UntilExpression { get; init; } = string.Empty;
    public string Delay { get; init; } = string.Empty;
    public string? Timeout { get; init; }
    public string OnTimeout { get; init; } = "fail";
    public DateTimeOffset StartedAt { get; init; }
}
