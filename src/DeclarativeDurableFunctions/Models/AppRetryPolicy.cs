using DeclarativeDurableFunctions.Engine;
using Microsoft.DurableTask;

namespace DeclarativeDurableFunctions.Models;

internal sealed class AppRetryPolicy
{
    public int MaxAttempts { get; init; }
    public string FirstRetryInterval { get; init; } = "PT1S";
    public string? MaxRetryInterval { get; init; }
    public double BackoffCoefficient { get; init; } = 1.0;

    public RetryPolicy ToSdkRetryPolicy()
    {
        var firstInterval = Iso8601DurationParser.Parse(FirstRetryInterval);
        var maxInterval = MaxRetryInterval != null
            ? Iso8601DurationParser.Parse(MaxRetryInterval)
            : (TimeSpan?)null;

        return new RetryPolicy(
            maxNumberOfAttempts: MaxAttempts,
            firstRetryInterval: firstInterval,
            backoffCoefficient: BackoffCoefficient,
            maxRetryInterval: maxInterval,
            retryTimeout: null);
    }
}
