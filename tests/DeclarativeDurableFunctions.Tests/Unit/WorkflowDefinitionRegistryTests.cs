using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;
using Xunit;

namespace DeclarativeDurableFunctions.Tests.Unit;

public class WorkflowDefinitionRegistryTests
{
    // ---- Round-trip: simple activity step ----

    [Fact]
    public void LoadFromYaml_SimpleActivity_ParsesCorrectly()
    {
        const string yaml = """
            workflow:
              name: OrderFulfillment
              steps:
                - name: ValidateOrder
                  activity: ValidateOrderActivity
                  input: "{{input}}"
                  output: validationResult
                  retry:
                    maxAttempts: 3
                    firstRetryInterval: PT5S
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "OrderFulfillment");

        Assert.Equal("OrderFulfillment", def.Name);
        Assert.Equal("OrderFulfillment", def.DisplayName);
        Assert.Single(def.Steps);

        var step = def.Steps[0];
        Assert.Equal("ValidateOrder", step.Name);
        Assert.Equal(StepType.Activity, step.Type);
        Assert.Equal("ValidateOrderActivity", step.ActivityName);
        Assert.Equal("validationResult", step.Output);

        Assert.NotNull(step.Retry);
        Assert.Equal(3, step.Retry!.MaxAttempts);
        Assert.Equal("PT5S", step.Retry.FirstRetryInterval);
    }

    // ---- Round-trip: foreach with activity ----

    [Fact]
    public void LoadFromYaml_ForeachActivity_ParsesSourceAndActivity()
    {
        const string yaml = """
            workflow:
              name: FulfillItems
              steps:
                - name: ReserveInventory
                  type: foreach
                  source: "{{input.lineItems}}"
                  activity: ReserveItemActivity
                  output: reservations
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "FulfillItems");

        var step = def.Steps[0];
        Assert.Equal(StepType.Foreach, step.Type);
        Assert.Equal("{{input.lineItems}}", step.Source);
        Assert.Equal("ReserveItemActivity", step.ActivityName);
        Assert.Null(step.WorkflowName);
        Assert.Equal("reservations", step.Output);
    }

    // ---- Round-trip: foreach with sub-orchestration ----

    [Fact]
    public void LoadFromYaml_ForeachSubOrchestration_ParsesWorkflowAndInstanceId()
    {
        const string yaml = """
            workflow:
              name: FulfillLineItems
              steps:
                - name: FulfillEach
                  type: foreach
                  source: "{{input.lineItems}}"
                  workflow: FulfillLineItem
                  instanceId: "{{$item.lineItemId}}"
                  output: fulfillmentResults
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "FulfillLineItems");

        var step = def.Steps[0];
        Assert.Equal(StepType.Foreach, step.Type);
        Assert.Equal("FulfillLineItem", step.WorkflowName);
        Assert.Equal("{{$item.lineItemId}}", step.InstanceId);
        Assert.Null(step.ActivityName);
    }

    // ---- Round-trip: parallel block ----

    [Fact]
    public void LoadFromYaml_ParallelBlock_ParsesChildSteps()
    {
        const string yaml = """
            workflow:
              name: Finalize
              steps:
                - name: FinalizeBlock
                  type: parallel
                  output: finalize
                  steps:
                    - name: SendConfirmation
                      activity: SendConfirmationEmailActivity
                    - name: UpdateLedger
                      type: sub-orchestration
                      workflow: LedgerUpdate
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "Finalize");

        var step = def.Steps[0];
        Assert.Equal(StepType.Parallel, step.Type);
        Assert.Equal("finalize", step.Output);
        Assert.Equal(2, step.Steps.Count);
        Assert.Equal("SendConfirmation", step.Steps[0].Name);
        Assert.Equal(StepType.Activity, step.Steps[0].Type);
        Assert.Equal("UpdateLedger", step.Steps[1].Name);
        Assert.Equal(StepType.SubOrchestration, step.Steps[1].Type);
        Assert.Equal("LedgerUpdate", step.Steps[1].WorkflowName);
    }

    // ---- Round-trip: wait-for-event ----

    [Fact]
    public void LoadFromYaml_WaitForEvent_ParsesTimeoutAndOnTimeout()
    {
        const string yaml = """
            workflow:
              name: WaitWorkflow
              steps:
                - name: WaitForApproval
                  type: wait-for-event
                  event: OrderApproved
                  timeout: P7D
                  on-timeout: continue
                  output: approval
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "WaitWorkflow");

        var step = def.Steps[0];
        Assert.Equal(StepType.WaitForEvent, step.Type);
        Assert.Equal("OrderApproved", step.EventName);
        Assert.Equal("P7D", step.Timeout);
        Assert.Equal("continue", step.OnTimeout);
        Assert.Equal("approval", step.Output);
    }

    [Fact]
    public void LoadFromYaml_WaitForEvent_DefaultOnTimeoutIsFail()
    {
        const string yaml = """
            workflow:
              name: WaitWorkflow
              steps:
                - name: WaitForApproval
                  type: wait-for-event
                  event: OrderApproved
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "WaitWorkflow").Steps[0];
        Assert.Equal("fail", step.OnTimeout);
    }

    // ---- Round-trip: switch ----

    [Fact]
    public void LoadFromYaml_Switch_ParsesCasesAndDefault()
    {
        const string yaml = """
            workflow:
              name: RouteWorkflow
              steps:
                - name: RouteByRegion
                  type: switch
                  on: "{{input.region}}"
                  cases:
                    EU:
                      - activity: EUActivity
                    US:
                      - activity: USActivity
                    default:
                      - activity: GlobalActivity
            """;

        var def = WorkflowDefinitionLoader.LoadFromYaml(yaml, "RouteWorkflow");

        var step = def.Steps[0];
        Assert.Equal(StepType.Switch, step.Type);
        Assert.Equal("{{input.region}}", step.SwitchOn);
        Assert.True(step.Cases.ContainsKey("EU"));
        Assert.True(step.Cases.ContainsKey("US"));
        Assert.True(step.Cases.ContainsKey("default"));
        Assert.Equal("EUActivity", step.Cases["EU"][0].ActivityName);
        Assert.Equal("GlobalActivity", step.Cases["default"][0].ActivityName);
    }

    // ---- Round-trip: sub-orchestration with explicit instanceId ----

    [Fact]
    public void LoadFromYaml_SubOrchestration_ParsesWorkflowAndInstanceId()
    {
        const string yaml = """
            workflow:
              name: RunSubOrch
              steps:
                - name: RunSubWorkflow
                  type: sub-orchestration
                  workflow: OrderValidation
                  instanceId: "{{input.orderId}}"
                  output: validationResult
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "RunSubOrch").Steps[0];

        Assert.Equal(StepType.SubOrchestration, step.Type);
        Assert.Equal("OrderValidation", step.WorkflowName);
        Assert.Equal("{{input.orderId}}", step.InstanceId);
    }

    // ---- Round-trip: condition on step ----

    [Fact]
    public void LoadFromYaml_StepWithCondition_ParsesConditionString()
    {
        const string yaml = """
            workflow:
              name: ConditionalWorkflow
              steps:
                - name: ChargeLateFee
                  activity: ChargeLateFeeActivity
                  condition: "{{approval.daysWaited > 3}}"
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "ConditionalWorkflow").Steps[0];
        Assert.Equal("{{approval.daysWaited > 3}}", step.Condition);
    }

    // ---- Validation: output: on parallel child is rejected at load time ----

    [Fact]
    public void LoadFromYaml_ParallelChildWithOutput_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadParallel
              steps:
                - name: Block
                  type: parallel
                  steps:
                    - name: Child
                      activity: ChildActivity
                      output: childResult
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadParallel"));
        Assert.Contains("output:", ex.Message);
        Assert.Contains("parallel", ex.Message);
    }

    // ---- Validation: missing workflow key ----

    [Fact]
    public void LoadFromYaml_MissingWorkflowKey_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            steps:
              - name: SomeStep
                activity: SomeActivity
            """;

        Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "Missing"));
    }

    // ---- Validation: missing steps key ----

    [Fact]
    public void LoadFromYaml_MissingSteps_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: NoSteps
            """;

        Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "NoSteps"));
    }

    // ---- Validation: unknown step type ----

    [Fact]
    public void LoadFromYaml_UnknownStepType_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadType
              steps:
                - name: WeirdStep
                  type: banana
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadType"));
        Assert.Contains("banana", ex.Message);
    }

    // ---- Validation: foreach without source ----

    [Fact]
    public void LoadFromYaml_ForeachWithoutSource_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: NoSource
              steps:
                - name: Process
                  type: foreach
                  activity: ProcessActivity
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "NoSource"));
        Assert.Contains("source", ex.Message);
    }

    // ---- Validation: wait-for-event without event name ----

    [Fact]
    public void LoadFromYaml_WaitForEventWithoutEvent_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: NoEvent
              steps:
                - name: Wait
                  type: wait-for-event
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "NoEvent"));
        Assert.Contains("event", ex.Message);
    }

    // ---- Validation: switch without 'on' ----

    [Fact]
    public void LoadFromYaml_SwitchWithoutOn_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: NoOn
              steps:
                - name: Route
                  type: switch
                  cases:
                    EU:
                      - activity: EUActivity
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "NoOn"));
        Assert.Contains("on", ex.Message);
    }

    // ---- Validation: invalid on-timeout value ----

    [Fact]
    public void LoadFromYaml_InvalidOnTimeout_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadTimeout
              steps:
                - name: Wait
                  type: wait-for-event
                  event: SomeEvent
                  on-timeout: skip
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadTimeout"));
        Assert.Contains("on-timeout", ex.Message);
    }

    // ---- Registry: Get and TryGet ----

    [Fact]
    public void Registry_Get_ReturnsDefinition_WhenWorkflowExists()
    {
        var def = new WorkflowDefinition { Name = "MyWorkflow", Steps = [] };
        var registry = new WorkflowDefinitionRegistry(new Dictionary<string, WorkflowDefinition>
        {
            ["MyWorkflow"] = def
        });

        var result = registry.Get("MyWorkflow");
        Assert.Equal("MyWorkflow", result.Name);
    }

    [Fact]
    public void Registry_Get_ThrowsWorkflowDefinitionException_WhenNotFound()
    {
        var registry = new WorkflowDefinitionRegistry(new Dictionary<string, WorkflowDefinition>());

        var ex = Assert.Throws<WorkflowDefinitionException>(() => registry.Get("Missing"));
        Assert.Equal("Missing", ex.WorkflowName);
    }

    [Fact]
    public void Registry_TryGet_ReturnsTrueAndDefinition_WhenFound()
    {
        var def = new WorkflowDefinition { Name = "MyWorkflow", Steps = [] };
        var registry = new WorkflowDefinitionRegistry(new Dictionary<string, WorkflowDefinition>
        {
            ["MyWorkflow"] = def
        });

        var found = registry.TryGet("MyWorkflow", out var result);
        Assert.True(found);
        Assert.NotNull(result);
    }

    [Fact]
    public void Registry_TryGet_ReturnsFalse_WhenNotFound()
    {
        var registry = new WorkflowDefinitionRegistry(new Dictionary<string, WorkflowDefinition>());

        var found = registry.TryGet("Missing", out var result);
        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void Registry_WorkflowNames_ContainsAllLoadedWorkflows()
    {
        var registry = new WorkflowDefinitionRegistry(new Dictionary<string, WorkflowDefinition>
        {
            ["Alpha"] = new WorkflowDefinition { Name = "Alpha", Steps = [] },
            ["Beta"] = new WorkflowDefinition { Name = "Beta", Steps = [] }
        });

        Assert.Contains("Alpha", registry.WorkflowNames);
        Assert.Contains("Beta", registry.WorkflowNames);
        Assert.Equal(2, registry.WorkflowNames.Count);
    }

    // ---- Round-trip: poll step ----

    [Fact]
    public void LoadFromYaml_PollStep_ParsesAllFields()
    {
        const string yaml = """
            workflow:
              name: PollWorkflow
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  input: "{{input.correlationId}}"
                  output: statusResult
                  until: "{{statusResult.status == 'Complete'}}"
                  delay: PT100M
                  timeout: PT30D
                  on-timeout: continue
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "PollWorkflow").Steps[0];

        Assert.Equal(StepType.Poll, step.Type);
        Assert.Equal("CheckStatusActivity", step.ActivityName);
        Assert.Equal("statusResult", step.Output);
        Assert.Equal("{{statusResult.status == 'Complete'}}", step.Until);
        Assert.Equal("PT100M", step.Delay);
        Assert.Equal("PT30D", step.Timeout);
        Assert.Equal("continue", step.OnTimeout);
    }

    [Fact]
    public void LoadFromYaml_PollStep_OptionalTimeout_IsNull_DefaultOnTimeoutIsFail()
    {
        const string yaml = """
            workflow:
              name: PollWorkflow
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  output: statusResult
                  until: "{{statusResult != null}}"
                  delay: PT30S
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "PollWorkflow").Steps[0];

        Assert.Equal(StepType.Poll, step.Type);
        Assert.Null(step.Timeout);
        Assert.Equal("fail", step.OnTimeout);
    }

    // ---- Validation: poll required fields ----

    [Fact]
    public void LoadFromYaml_PollWithoutActivity_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadPoll
              steps:
                - name: WaitForCompletion
                  type: poll
                  output: statusResult
                  until: "{{statusResult.status == 'Complete'}}"
                  delay: PT30S
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadPoll"));
        Assert.Contains("activity", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_PollWithoutOutput_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadPoll
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  until: "{{statusResult.status == 'Complete'}}"
                  delay: PT30S
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadPoll"));
        Assert.Contains("output", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_PollWithoutUntil_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadPoll
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  output: statusResult
                  delay: PT30S
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadPoll"));
        Assert.Contains("until", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_PollWithoutDelay_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadPoll
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  output: statusResult
                  until: "{{statusResult.status == 'Complete'}}"
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadPoll"));
        Assert.Contains("delay", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_PollWithInvalidOnTimeout_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadPoll
              steps:
                - name: WaitForCompletion
                  type: poll
                  activity: CheckStatusActivity
                  output: statusResult
                  until: "{{statusResult.status == 'Complete'}}"
                  delay: PT30S
                  on-timeout: skip
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadPoll"));
        Assert.Contains("on-timeout", ex.Message);
    }

    // ---- Round-trip: trigger-and-wait ----

    [Fact]
    public void LoadFromYaml_TriggerAndWait_ParsesAllFields()
    {
        const string yaml = """
            workflow:
              name: OrderWorkflow
              steps:
                - name: SendToProcessor
                  type: trigger-and-wait
                  activity: SendOrderToProcessorActivity
                  input: "{{input.order}}"
                  event: OrderProcessed
                  timeout: PT60M
                  on-timeout: continue
                  output: processingResult
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "OrderWorkflow").Steps[0];

        Assert.Equal(StepType.TriggerAndWait, step.Type);
        Assert.Equal("SendOrderToProcessorActivity", step.ActivityName);
        Assert.Equal("OrderProcessed", step.EventName);
        Assert.Equal("PT60M", step.Timeout);
        Assert.Equal("continue", step.OnTimeout);
        Assert.Equal("processingResult", step.Output);
    }

    [Fact]
    public void LoadFromYaml_TriggerAndWait_OptionalFields_Absent_DefaultsApplied()
    {
        const string yaml = """
            workflow:
              name: OrderWorkflow
              steps:
                - name: SendToProcessor
                  type: trigger-and-wait
                  activity: SendOrderToProcessorActivity
                  event: OrderProcessed
            """;

        var step = WorkflowDefinitionLoader.LoadFromYaml(yaml, "OrderWorkflow").Steps[0];

        Assert.Equal(StepType.TriggerAndWait, step.Type);
        Assert.Null(step.Timeout);
        Assert.Equal("fail", step.OnTimeout);
        Assert.Null(step.Output);
    }

    // ---- Validation: trigger-and-wait required fields ----

    [Fact]
    public void LoadFromYaml_TriggerAndWaitWithoutActivity_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadWorkflow
              steps:
                - name: SendToProcessor
                  type: trigger-and-wait
                  event: OrderProcessed
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadWorkflow"));
        Assert.Contains("activity", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_TriggerAndWaitWithoutEvent_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadWorkflow
              steps:
                - name: SendToProcessor
                  type: trigger-and-wait
                  activity: SendOrderToProcessorActivity
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadWorkflow"));
        Assert.Contains("event", ex.Message);
    }

    [Fact]
    public void LoadFromYaml_TriggerAndWaitWithInvalidOnTimeout_ThrowsWorkflowDefinitionException()
    {
        const string yaml = """
            workflow:
              name: BadWorkflow
              steps:
                - name: SendToProcessor
                  type: trigger-and-wait
                  activity: SendOrderToProcessorActivity
                  event: OrderProcessed
                  on-timeout: skip
            """;

        var ex = Assert.Throws<WorkflowDefinitionException>(() =>
            WorkflowDefinitionLoader.LoadFromYaml(yaml, "BadWorkflow"));
        Assert.Contains("on-timeout", ex.Message);
    }
}
