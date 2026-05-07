namespace DeclarativeDurableFunctions.Models;

enum StepType
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
