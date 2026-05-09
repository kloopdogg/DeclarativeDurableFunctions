using DeclarativeDurableFunctions.Exceptions;

namespace DeclarativeDurableFunctions.Engine;

static class Iso8601DurationParser
{
    public static TimeSpan Parse(string duration)
    {
        if (string.IsNullOrEmpty(duration))
        {
            throw new WorkflowDefinitionException("ISO 8601 duration cannot be empty.");
        }

        if (!duration.StartsWith('P'))
        {
            throw new WorkflowDefinitionException($"Invalid ISO 8601 duration '{duration}': must start with 'P'.");
        }

        double totalSeconds = 0.0;
        int i = 1;
        bool inTime = false;

        while (i < duration.Length)
        {
            if (duration[i] == 'T')
            {
                inTime = true;
                i++;
                continue;
            }

            int start = i;
            while (i < duration.Length && (char.IsDigit(duration[i]) || duration[i] == '.'))
            {
                i++;
            }

            if (i == start || i >= duration.Length)
            {
                throw new WorkflowDefinitionException($"Invalid ISO 8601 duration '{duration}'.");
            }

            string numStr = duration[start..i];
            if (!double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                throw new WorkflowDefinitionException($"Invalid ISO 8601 duration '{duration}': invalid number '{numStr}'.");
            }

            char unit = duration[i++];
            totalSeconds += (unit, inTime) switch
            {
                ('Y', _)      => value * 365 * 24 * 3600,
                ('M', false)  => value * 30  * 24 * 3600,
                ('W', _)      => value * 7   * 24 * 3600,
                ('D', _)      => value        * 24 * 3600,
                ('H', true)   => value * 3600,
                ('M', true)   => value * 60,
                ('S', true)   => value,
                _ => throw new WorkflowDefinitionException($"Invalid ISO 8601 duration '{duration}': unknown unit '{unit}'.")
            };
        }

        return TimeSpan.FromSeconds(totalSeconds);
    }
}
