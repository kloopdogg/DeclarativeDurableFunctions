using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Models;

internal sealed class AppRetryPolicy
{
    public int MaxAttempts { get; init; }
    public string FirstRetryInterval { get; init; } = "PT1S";
    public string? MaxRetryInterval { get; init; }
    public double BackoffCoefficient { get; init; } = 1.0;

    public RetryPolicy ToSdkRetryPolicy() => throw new NotImplementedException();
}
