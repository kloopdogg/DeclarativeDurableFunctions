namespace DeclarativeDurableFunctions.Models;

internal sealed class RetryPolicy
{
    public int MaxAttempts { get; init; }
    public string FirstRetryInterval { get; init; } = "PT1S";
    public string? MaxRetryInterval { get; init; }
    public double BackoffCoefficient { get; init; } = 1.0;
}
