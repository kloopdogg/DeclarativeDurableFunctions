using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using DeclarativeDurableFunctions.Models;
using Microsoft.DurableTask;
using NSubstitute;
using Xunit;

namespace DeclarativeDurableFunctions.Tests.Unit;

public class WorkflowRunnerTests
{
    // ---- Helpers ----

    private static (TaskOrchestrationContext context, WorkflowExecutionContext execCtx) MakeContext(
        string inputJson = "{}",
        string instanceId = "test-instance",
        string? parentInstanceId = null)
    {
        var context = Substitute.For<TaskOrchestrationContext>();
        context.InstanceId.Returns(instanceId);
        context.Parent.Returns(parentInstanceId != null
            ? new ParentOrchestrationInstance(default, parentInstanceId)
            : null);
        context.NewGuid().Returns(Guid.Empty);
        context.CurrentUtcDateTime.Returns(DateTime.UtcNow);

        var input = JsonDocument.Parse(inputJson).RootElement;
        var execCtx = new WorkflowExecutionContext(input, context);
        return (context, execCtx);
    }

    private static WorkflowDefinition MakeDef(params StepDefinition[] steps)
        => new WorkflowDefinition { Name = "TestWorkflow", Steps = steps };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Activity ----

    [Fact]
    public async Task Activity_CallsActivityAsync_WithCorrectName()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Validate",
            Type = StepType.Activity,
            ActivityName = "ValidateOrderActivity"
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "ValidateOrderActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task Activity_StoresResult_UnderOutputName()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"value":42}""")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Compute",
            Type = StepType.Activity,
            ActivityName = "ComputeActivity",
            Output = "computeResult"
        }), execCtx);

        Assert.True(execCtx.HasOutput("computeResult"));
        var stored = (JsonElement)execCtx.GetOutput("computeResult")!;
        Assert.Equal(42, stored.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Activity_WithRetry_PassesNonNullOptions()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "RetryStep",
            Type = StepType.Activity,
            ActivityName = "RetryActivity",
            Retry = new AppRetryPolicy { MaxAttempts = 3, FirstRetryInterval = "PT5S" }
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Any<TaskName>(),
            Arg.Any<object?>(),
            Arg.Is<TaskOptions?>(o => o != null));
    }

    [Fact]
    public async Task Activity_WithoutRetry_PassesNullOptions()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "NoRetryStep",
            Type = StepType.Activity,
            ActivityName = "NoRetryActivity"
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Any<TaskName>(),
            Arg.Any<object?>(),
            Arg.Is<TaskOptions?>(o => o == null));
    }

    // ---- SubOrchestration ----

    [Fact]
    public async Task SubOrchestration_CallsSubOrchestratorAsync_WithWorkflowName()
    {
        var (context, execCtx) = MakeContext();
        context.CallSubOrchestratorAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "RunSubWorkflow",
            Type = StepType.SubOrchestration,
            WorkflowName = "OrderValidation"
        }), execCtx);

        await context.Received(1).CallSubOrchestratorAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "OrderValidation"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task SubOrchestration_InstanceId_HasCorrectFormat()
    {
        var (context, execCtx) = MakeContext(instanceId: "parent-instance");
        context.NewGuid().Returns(Guid.Empty);
        SubOrchestrationOptions? capturedOpts = null;
        context.CallSubOrchestratorAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(callInfo =>
            {
                capturedOpts = callInfo[2] as SubOrchestrationOptions;
                return Task.FromResult(Json("{}"));
            });

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "RunSubWorkflow",
            Type = StepType.SubOrchestration,
            WorkflowName = "OrderValidation"
        }), execCtx);

        Assert.NotNull(capturedOpts);
        Assert.Equal($"parent-instance:RunSubWorkflow:{Guid.Empty}", capturedOpts.InstanceId);
    }

    // ---- Foreach ----

    [Fact]
    public async Task Foreach_Activity_InvokesActivityForEachItem()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"items":[1,2,3]}""");
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "ProcessItems",
            Type = StepType.Foreach,
            Source = "{{input.items}}",
            ActivityName = "ProcessItemActivity",
            Output = "results"
        }), execCtx);

        await context.Received(3).CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "ProcessItemActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task Foreach_Activity_StoresResultsAsArray()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"items":[1,2]}""");
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"done":true}""")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Process",
            Type = StepType.Foreach,
            Source = "{{input.items}}",
            ActivityName = "ProcessActivity",
            Output = "results"
        }), execCtx);

        Assert.True(execCtx.HasOutput("results"));
        var stored = (JsonElement)execCtx.GetOutput("results")!;
        Assert.Equal(JsonValueKind.Array, stored.ValueKind);
        Assert.Equal(2, stored.GetArrayLength());
    }

    // ---- Parallel ----

    [Fact]
    public async Task Parallel_BranchResults_AggregatedUnderOutputName()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(
                Arg.Is<TaskName>(n => n.Name == "ActivityA"), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"label":"a"}""")));
        context.CallActivityAsync<JsonElement>(
                Arg.Is<TaskName>(n => n.Name == "ActivityB"), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"label":"b"}""")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Finalize",
            Type = StepType.Parallel,
            Output = "finalize",
            Steps =
            [
                new StepDefinition { Name = "BranchA", Type = StepType.Activity, ActivityName = "ActivityA" },
                new StepDefinition { Name = "BranchB", Type = StepType.Activity, ActivityName = "ActivityB" }
            ]
        }), execCtx);

        Assert.True(execCtx.HasOutput("finalize"));
        var agg = (JsonElement)execCtx.GetOutput("finalize")!;
        Assert.Equal("a", agg.GetProperty("BranchA").GetProperty("label").GetString());
        Assert.Equal("b", agg.GetProperty("BranchB").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Parallel_IndividualBranchOutputs_NotLeakedToParentContext()
    {
        var (context, execCtx) = MakeContext();
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Block",
            Type = StepType.Parallel,
            Output = "blockResult",
            Steps =
            [
                new StepDefinition { Name = "Child", Type = StepType.Activity, ActivityName = "ChildActivity" }
            ]
        }), execCtx);

        // Branch result is stored inside the aggregate, not as a direct output on the parent context
        Assert.False(execCtx.HasOutput("Child"));
        Assert.True(execCtx.HasOutput("blockResult"));
    }

    [Fact]
    public async Task Parallel_BranchCannotObserveSiblingWrite_SeesFrozenParentSnapshot()
    {
        // Arrange: parent context has "BranchA" pre-set to a sentinel before the block runs.
        var (context, execCtx) = MakeContext();
        var parentSnapshot = Json("""{"sentinel":"parent"}""");
        execCtx.SetOutput("BranchA", parentSnapshot);

        // Branch A's activity returns a DIFFERENT value — this goes into branchScopes[0] under "BranchA".
        context.CallActivityAsync<JsonElement>(
                Arg.Is<TaskName>(n => n.Name == "ActivityA"), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"sentinel":"from-branch-a"}""")));

        // Branch B's input is "{{BranchA}}" — capture what the runner actually resolves for it.
        object? capturedInput = null;
        context.CallActivityAsync<JsonElement>(
                Arg.Is<TaskName>(n => n.Name == "ActivityB"), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(callInfo =>
            {
                capturedInput = callInfo[1];
                return Task.FromResult(Json("{}"));
            });

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Block",
            Type = StepType.Parallel,
            Output = "blockResult",
            Steps =
            [
                new StepDefinition { Name = "BranchA", Type = StepType.Activity, ActivityName = "ActivityA" },
                new StepDefinition { Name = "BranchB", Type = StepType.Activity, ActivityName = "ActivityB", Input = "{{BranchA}}" }
            ]
        }), execCtx);

        // Branch B must see the parent's frozen snapshot ("parent"), not what Branch A wrote ("from-branch-a").
        Assert.NotNull(capturedInput);
        var resolved = (JsonElement)capturedInput!;
        Assert.Equal("parent", resolved.GetProperty("sentinel").GetString());
    }

    [Fact]
    public async Task Parallel_ConditionFalseBranch_NullInAggregate()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"runB":false}""");
        context.CallActivityAsync<JsonElement>(
                Arg.Is<TaskName>(n => n.Name == "ActivityA"), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("""{"label":"a"}""")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Block",
            Type = StepType.Parallel,
            Output = "blockResult",
            Steps =
            [
                new StepDefinition { Name = "BranchA", Type = StepType.Activity, ActivityName = "ActivityA" },
                new StepDefinition { Name = "BranchB", Type = StepType.Activity, ActivityName = "ActivityB", Condition = "{{input.runB}}" }
            ]
        }), execCtx);

        var agg = (JsonElement)execCtx.GetOutput("blockResult")!;
        Assert.NotEqual(JsonValueKind.Null, agg.GetProperty("BranchA").ValueKind);
        Assert.Equal(JsonValueKind.Null, agg.GetProperty("BranchB").ValueKind);
    }

    [Fact]
    public async Task Parallel_WaitForEventTimeoutContinue_NullInAggregate()
    {
        var (context, execCtx) = MakeContext();
        var neverEvent = new TaskCompletionSource<JsonElement>().Task;
        context.WaitForExternalEvent<JsonElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(neverEvent);
        context.CreateTimer(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Block",
            Type = StepType.Parallel,
            Output = "blockResult",
            Steps =
            [
                new StepDefinition
                {
                    Name = "WaitBranch",
                    Type = StepType.WaitForEvent,
                    EventName = "SomeEvent",
                    Timeout = "PT1H",
                    OnTimeout = "continue"
                }
            ]
        }), execCtx);

        var agg = (JsonElement)execCtx.GetOutput("blockResult")!;
        Assert.Equal(JsonValueKind.Null, agg.GetProperty("WaitBranch").ValueKind);
    }

    // ---- WaitForEvent ----

    [Fact]
    public async Task WaitForEvent_WithoutTimeout_EventReceived_StoresPayload()
    {
        var (context, execCtx) = MakeContext();
        var payload = Json("""{"approved":true}""");
        context.WaitForExternalEvent<JsonElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(payload));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "WaitForApproval",
            Type = StepType.WaitForEvent,
            EventName = "OrderApproved",
            Output = "approval"
        }), execCtx);

        Assert.True(execCtx.HasOutput("approval"));
        var stored = (JsonElement)execCtx.GetOutput("approval")!;
        Assert.True(stored.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task WaitForEvent_WithTimeout_EventReceived_StoresPayload()
    {
        var (context, execCtx) = MakeContext();
        var payload = Json("""{"approved":true}""");
        context.WaitForExternalEvent<JsonElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(payload));
        context.CreateTimer(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource().Task); // never fires

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "WaitForApproval",
            Type = StepType.WaitForEvent,
            EventName = "OrderApproved",
            Timeout = "PT1H",
            OnTimeout = "fail",
            Output = "approval"
        }), execCtx);

        Assert.True(execCtx.HasOutput("approval"));
        var stored = (JsonElement)execCtx.GetOutput("approval")!;
        Assert.True(stored.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task WaitForEvent_TimeoutContinue_StoresExplicitNull()
    {
        var (context, execCtx) = MakeContext();
        context.WaitForExternalEvent<JsonElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<JsonElement>().Task); // never fires
        context.CreateTimer(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask); // fires immediately

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "WaitForApproval",
            Type = StepType.WaitForEvent,
            EventName = "OrderApproved",
            Timeout = "PT1H",
            OnTimeout = "continue",
            Output = "approval"
        }), execCtx);

        // Key: the output key IS present with an explicit null — not a missing key
        Assert.True(execCtx.HasOutput("approval"));
        Assert.Null(execCtx.GetOutput("approval"));
    }

    [Fact]
    public async Task WaitForEvent_TimeoutFail_ThrowsWorkflowTimeoutException()
    {
        var (context, execCtx) = MakeContext();
        context.WaitForExternalEvent<JsonElement>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<JsonElement>().Task);
        context.CreateTimer(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<WorkflowTimeoutException>(() =>
            WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
            {
                Name = "WaitForApproval",
                Type = StepType.WaitForEvent,
                EventName = "OrderApproved",
                Timeout = "PT1H",
                OnTimeout = "fail"
            }), execCtx));
    }

    // ---- Switch ----

    [Fact]
    public async Task Switch_MatchingCase_ExecutesCaseSteps()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"region":"EU"}""");
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Route",
            Type = StepType.Switch,
            SwitchOn = "{{input.region}}",
            Cases = new Dictionary<string, IReadOnlyList<StepDefinition>>
            {
                ["EU"] = [new StepDefinition { Name = "EUStep", Type = StepType.Activity, ActivityName = "EUActivity" }],
                ["US"] = [new StepDefinition { Name = "USStep", Type = StepType.Activity, ActivityName = "USActivity" }]
            }
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "EUActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
        await context.DidNotReceive().CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "USActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task Switch_NoMatch_ExecutesDefaultCase()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"region":"APAC"}""");
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Route",
            Type = StepType.Switch,
            SwitchOn = "{{input.region}}",
            Cases = new Dictionary<string, IReadOnlyList<StepDefinition>>
            {
                ["EU"] = [new StepDefinition { Name = "EUStep", Type = StepType.Activity, ActivityName = "EUActivity" }],
                ["default"] = [new StepDefinition { Name = "DefaultStep", Type = StepType.Activity, ActivityName = "DefaultActivity" }]
            }
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "DefaultActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
        await context.DidNotReceive().CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "EUActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task Switch_NoMatchNoDefault_ContinuesSilently()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"region":"APAC"}""");

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "Route",
            Type = StepType.Switch,
            SwitchOn = "{{input.region}}",
            Cases = new Dictionary<string, IReadOnlyList<StepDefinition>>
            {
                ["EU"] = [new StepDefinition { Name = "EUStep", Type = StepType.Activity, ActivityName = "EUActivity" }]
            }
        }), execCtx);

        await context.DidNotReceive().CallActivityAsync<JsonElement>(
            Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    // ---- Condition ----

    [Fact]
    public async Task Condition_False_SkipsStep()
    {
        var (context, execCtx) = MakeContext();
        // "approval" is not set in execCtx — EvaluateBool returns false for unset variable

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "ChargeLateFee",
            Type = StepType.Activity,
            ActivityName = "ChargeLateFeeActivity",
            Condition = "{{approval}}"
        }), execCtx);

        await context.DidNotReceive().CallActivityAsync<JsonElement>(
            Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }

    [Fact]
    public async Task Condition_True_ExecutesStep()
    {
        var (context, execCtx) = MakeContext(inputJson: """{"approved":true}""");
        context.CallActivityAsync<JsonElement>(Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<TaskOptions?>())
            .Returns(Task.FromResult(Json("{}")));

        await WorkflowRunner.RunAsync(context, MakeDef(new StepDefinition
        {
            Name = "ChargeLateFee",
            Type = StepType.Activity,
            ActivityName = "ChargeLateFeeActivity",
            Condition = "{{input.approved}}"
        }), execCtx);

        await context.Received(1).CallActivityAsync<JsonElement>(
            Arg.Is<TaskName>(n => n.Name == "ChargeLateFeeActivity"),
            Arg.Any<object?>(), Arg.Any<TaskOptions?>());
    }
}
