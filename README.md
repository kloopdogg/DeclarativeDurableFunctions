# DeclarativeDurableFunctions

A NuGet package that brings declarative, YAML-driven workflow definitions to Azure Durable Functions — so you never have to write an orchestrator function again.

## The idea

Azure Durable Functions are powerful. Orchestrator function code is not fun to write or read. This project puts a thin declarative layer on top of the Durable Functions runtime: you describe your workflow in YAML, write your activity functions in C# as normal, and the framework handles orchestration.

```yaml
workflow:
  name: OrderFulfillment
  steps:
    - name: ValidateOrder
      activity: ValidateOrderActivity
      output: validation

    - name: FulfillLineItems
      type: foreach
      source: "{{input.lineItems}}"
      workflow: FulfillLineItem
      input:
        parent:
          orchestrationId: "{{orchestration.instanceId}}"
          correlationId: "{{input.correlationId}}"
        data: "{{$item}}"
      output: fulfillmentResults

    - name: WaitForApproval
      type: wait-for-event
      event: OrderApproved
      timeout: P7D
      output: approval

    - name: Finalize
      type: parallel
      steps:
        - activity: SendConfirmationEmailActivity
        - activity: UpdateInventoryActivity
```

Your orchestrator function becomes a stub:

```csharp
public class OrderFulfillmentOrchestrator(IWorkflowDefinitionRegistry registry)
{
    [Function("OrderFulfillment")]
    public Task RunAsync([OrchestrationTrigger] TaskOrchestrationContext context)
        => context.RunWorkflowAsync(registry);
}
```

## Supported workflow patterns

- Sequential activity calls with retry policies
- Sub-orchestrations (call a child workflow and wait)
- Fan-out over a collection → activities → `Task.WhenAll`
- Fan-out over a collection → sub-orchestrations → `Task.WhenAll`
- Parallel blocks with mixed step types
- External event waits (human approval, Service Bus callbacks, external agent completion)
- Conditional steps and switch/case routing
- Any combination of the above, nested arbitrarily

## Status

Early design phase. See [`docs/vision.md`](docs/vision.md) for the full design including schema reference, expression language, engine architecture, and the input envelope convention.

## Target platform

- Azure Functions — .NET isolated worker model
- C# first; Python and JS are future considerations
