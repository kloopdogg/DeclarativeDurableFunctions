namespace DeclarativeDurableFunctions.Models;

internal enum StepType
{
    Activity,
    SubOrchestration,
    Foreach,
    Parallel,
    WaitForEvent,
    Switch,
    Poll,
    TriggerAndWait,
    Loop,
}
