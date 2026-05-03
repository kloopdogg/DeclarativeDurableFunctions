using System.Text.Json;
using DeclarativeDurableFunctions.Engine;
using DeclarativeDurableFunctions.Exceptions;
using NSubstitute;
using Microsoft.DurableTask;
using Xunit;

namespace DeclarativeDurableFunctions.Tests.Unit;

public class ExpressionEvaluatorTests
{
    // ---- Helpers ----

    private static WorkflowExecutionContext MakeCtx(
        string inputJson = "{}",
        string instanceId = "test-instance",
        string? parentInstanceId = null,
        Dictionary<string, object?>? outputs = null,
        JsonElement? iterationItem = null,
        int? iterationIndex = null)
    {
        var orchestrationCtx = Substitute.For<TaskOrchestrationContext>();
        orchestrationCtx.InstanceId.Returns(instanceId);
        orchestrationCtx.Parent.Returns(parentInstanceId != null
            ? CreateParentInfo(parentInstanceId)
            : null);

        var input = JsonDocument.Parse(inputJson).RootElement;
        var ctx = new WorkflowExecutionContext(input, orchestrationCtx);

        if (outputs != null)
            foreach (var (k, v) in outputs)
                ctx.SetOutput(k, v);

        if (iterationItem.HasValue)
            ctx = ctx.CreateIterationScope(iterationItem.Value, iterationIndex ?? 0);

        return ctx;
    }

    private static ParentOrchestrationInstance CreateParentInfo(string instanceId)
        => new ParentOrchestrationInstance(default, instanceId);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Regression: EvaluateBool bare-path conditions must not throw ----

    [Fact]
    public void EvaluateBool_BarePathUnsetVariable_ReturnsFalse()
    {
        // approval has not been set; condition should be falsy, not throw
        var ctx = MakeCtx();
        var result = ExpressionEvaluator.EvaluateBool("{{approval}}", ctx);
        Assert.False(result);
    }

    [Fact]
    public void EvaluateBool_BarePathMissingProperty_ReturnsFalse()
    {
        // input.optionalFlag does not exist in the input JSON
        var ctx = MakeCtx(inputJson: "{}");
        var result = ExpressionEvaluator.EvaluateBool("{{input.optionalFlag}}", ctx);
        Assert.False(result);
    }

    [Fact]
    public void EvaluateBool_BarePathSetVariable_ReturnsTrue()
    {
        var ctx = MakeCtx(outputs: new() { ["approval"] = "yes" });
        var result = ExpressionEvaluator.EvaluateBool("{{approval}}", ctx);
        Assert.True(result);
    }

    [Fact]
    public void EvaluateBool_BarePathNullVariable_ReturnsFalse()
    {
        // explicit null stored (e.g. on-timeout: continue) → falsy
        var ctx = MakeCtx(outputs: new() { ["approval"] = null });
        var result = ExpressionEvaluator.EvaluateBool("{{approval}}", ctx);
        Assert.False(result);
    }

    // ---- Regression: Evaluate (non-condition) unset variable must throw ----

    [Fact]
    public void Evaluate_UnsetVariableInInputExpression_Throws()
    {
        // Non-condition path must surface missing variables as errors
        var ctx = MakeCtx();
        Assert.Throws<WorkflowExpressionException>(() =>
            ExpressionEvaluator.Evaluate("{{missing}}", ctx));
    }

    // ---- Whole-value single expression preserves type ----

    [Fact]
    public void Evaluate_WholeValueObject_PreservesJsonElement()
    {
        var ctx = MakeCtx(inputJson: """{"a":1}""");
        var result = ExpressionEvaluator.Evaluate("{{input}}", ctx);
        Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Object, ((JsonElement)result!).ValueKind);
    }

    [Fact]
    public void Evaluate_WholeValueArray_PreservesJsonElement()
    {
        var ctx = MakeCtx(inputJson: """{"items":[1,2,3]}""");
        var result = ExpressionEvaluator.Evaluate("{{input.items}}", ctx);
        Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, ((JsonElement)result!).ValueKind);
    }

    [Fact]
    public void Evaluate_WholeValueNumber_PreservesNumericType()
    {
        var ctx = MakeCtx(inputJson: """{"total":42}""");
        var result = ExpressionEvaluator.Evaluate("{{input.total}}", ctx);
        Assert.Equal(42L, result);
    }

    [Fact]
    public void Evaluate_WholeValueBool_PreservesBoolType()
    {
        var ctx = MakeCtx(inputJson: """{"flag":true}""");
        var result = ExpressionEvaluator.Evaluate("{{input.flag}}", ctx);
        Assert.Equal(true, result);
    }

    // ---- Embedded interpolation stringifies ----

    [Fact]
    public void Evaluate_EmbeddedInterpolation_ReturnsString()
    {
        var ctx = MakeCtx(inputJson: """{"id":"X1"}""");
        var result = ExpressionEvaluator.Evaluate("Order {{input.id}} received", ctx);
        Assert.Equal("Order X1 received", result);
    }

    [Fact]
    public void Evaluate_EmbeddedInterpolationNumber_StringifiesNumber()
    {
        var ctx = MakeCtx(inputJson: """{"count":5}""");
        var result = ExpressionEvaluator.Evaluate("Count: {{input.count}}", ctx);
        Assert.Equal("Count: 5", result);
    }

    // ---- Property access ----

    [Fact]
    public void Evaluate_NestedPropertyAccess()
    {
        var ctx = MakeCtx(inputJson: """{"a":{"b":42}}""");
        var result = ExpressionEvaluator.Evaluate("{{input.a.b}}", ctx);
        Assert.Equal(42L, result);
    }

    // ---- Foreach variables ----

    [Fact]
    public void Evaluate_Item_ReturnsCurrentItem()
    {
        var item = Json("""{"name":"foo"}""");
        var ctx = MakeCtx().CreateIterationScope(item, 0);
        var result = ExpressionEvaluator.Evaluate("{{$item.name}}", ctx);
        Assert.Equal("foo", result);
    }

    [Fact]
    public void Evaluate_Index_ReturnsCurrentIndex()
    {
        var item = Json("{}");
        var ctx = MakeCtx().CreateIterationScope(item, 2);
        var result = ExpressionEvaluator.Evaluate("{{$index}}", ctx);
        Assert.Equal(2, result);
    }

    [Fact]
    public void Evaluate_ItemOutsideForeach_Throws()
    {
        var ctx = MakeCtx();
        Assert.Throws<WorkflowExpressionException>(() =>
            ExpressionEvaluator.Evaluate("{{$item}}", ctx));
    }

    // ---- Built-in orchestration variables ----

    [Fact]
    public void Evaluate_InstanceId_ReturnsContextInstanceId()
    {
        var ctx = MakeCtx(instanceId: "orch-abc");
        var result = ExpressionEvaluator.Evaluate("{{orchestration.instanceId}}", ctx);
        Assert.Equal("orch-abc", result);
    }

    [Fact]
    public void Evaluate_ParentInstanceId_WhenPresent()
    {
        var ctx = MakeCtx(parentInstanceId: "parent-xyz");
        var result = ExpressionEvaluator.Evaluate("{{orchestration.parentInstanceId}}", ctx);
        Assert.Equal("parent-xyz", result);
    }

    [Fact]
    public void Evaluate_ParentInstanceId_WhenAbsentReturnsNull()
    {
        var ctx = MakeCtx();
        var result = ExpressionEvaluator.Evaluate("{{orchestration.parentInstanceId}}", ctx);
        Assert.Null(result);
    }

    // ---- Step output reference ----

    [Fact]
    public void Evaluate_StepOutputReference_ReturnsStoredValue()
    {
        var output = Json("""{"result":99}""");
        var ctx = MakeCtx(outputs: new() { ["stepA"] = output });
        var result = ExpressionEvaluator.Evaluate("{{stepA.result}}", ctx);
        Assert.Equal(99L, result);
    }

    // ---- Conditions (EvaluateBool) ----

    [Fact]
    public void EvaluateBool_GreaterThan_True()
    {
        var ctx = MakeCtx(inputJson: """{"total":15}""");
        Assert.True(ExpressionEvaluator.EvaluateBool("{{input.total > 10}}", ctx));
    }

    [Fact]
    public void EvaluateBool_GreaterThan_False()
    {
        var ctx = MakeCtx(inputJson: """{"total":5}""");
        Assert.False(ExpressionEvaluator.EvaluateBool("{{input.total > 10}}", ctx));
    }

    [Fact]
    public void EvaluateBool_Equality_True()
    {
        var ctx = MakeCtx(inputJson: """{"region":"EU"}""");
        Assert.True(ExpressionEvaluator.EvaluateBool("""{{input.region == "EU"}}""", ctx));
    }

    [Fact]
    public void EvaluateBool_And_True()
    {
        var ctx = MakeCtx(inputJson: """{"a":1,"b":"x"}""");
        Assert.True(ExpressionEvaluator.EvaluateBool("{{input.a > 0 && input.b != null}}", ctx));
    }

    [Fact]
    public void EvaluateBool_MissingPropertyInComparison_ReturnsFalse()
    {
        var ctx = MakeCtx(inputJson: "{}");
        Assert.False(ExpressionEvaluator.EvaluateBool("{{input.missing > 0}}", ctx));
    }
}
