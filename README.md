# DeclarativeDurableFunctions

A NuGet package that brings declarative, YAML-driven workflow definitions to Azure Durable Functions — so you never have to write an orchestrator function again.

## The idea

Azure Durable Functions are powerful, but orchestrator code has some real problems:

- ❌ Tedious to write — replay constraints, determinism rules, and boilerplate everywhere
- ❌ Painful to review — the control flow is obscured by SDK ceremony
- ❌ Hard to hand off — new team members have to learn how Durable Functions replay works before they can touch it

None of that has anything to do with the business problem you're actually solving.

This project eliminates orchestrator code entirely. Describe your workflow in YAML, write your activity functions in C# as you normally would, and the framework drives the Durable Functions runtime underneath. The result:

- ✅ A workflow a non-engineer can read
- ✅ A diff a reviewer can audit in minutes
- ✅ A file a new team member can modify without first understanding how Durable Functions replay works

## Supported workflow patterns

- Sequential activity calls with retry policies
- Sub-orchestrations (call a child workflow and wait)
- Fan-out over a collection → activities → `Task.WhenAll`
- Fan-out over a collection → sub-orchestrations → `Task.WhenAll`
- Parallel blocks with mixed step types
- External event waits (human approval, Service Bus callbacks, external agent completion)
- Trigger-and-wait (fire a trigger activity and await an external callback, race-condition safe)
- Polling loops (call activity repeatedly until a condition is met, with `ContinueAsNew` for history safety)
- Retry loops (run any step sequence repeatedly with a long delay between attempts and an overall wall-clock timeout)
- Conditional steps and switch/case routing
- Any combination of the above, nested arbitrarily

## Install

```bash
dotnet add package DeclarativeDurableFunctions --version 0.1.0-alpha
```

## Quickstart

**1. Add your workflow YAML** to a `Workflows/` folder in your Azure Functions project:

```yaml
# Workflows/OrderFulfillment.yaml
workflow:
  name: Order Fulfillment
  steps:
    - name: ValidateOrder
      activity: ValidateOrderActivity
      input: "{{input}}"
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
      instanceId: "{{$item.lineItemId}}"
      output: fulfillmentResults

    - name: WaitForApproval
      type: wait-for-event
      event: OrderApproved
      timeout: P7D
      on-timeout: continue
      output: approval

    - name: Finalize
      type: parallel
      output: finalize
      steps:
        - name: SendConfirmation
          activity: SendConfirmationEmailActivity
          input: "{{input.customerEmail}}"
        - name: UpdateLedger
          activity: UpdateLedgerActivity
          input: "{{fulfillmentResults}}"
```

**2. Register the engine** in `Program.cs`:

```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddDeclarativeWorkflows(); // loads Workflows/*.yaml at startup
    })
    .Build();

await host.RunAsync();
```

That's it. The framework auto-discovers and registers `GenericOrchestrator`, `StartWorkflow` (`POST /api/workflows/{workflowName}`), and `EventTrigger` (`POST /api/events/{instanceId}/{eventName}`) from the library assembly — no per-workflow stubs required. Write your activity functions normally — the YAML drives the orchestration.

See [`src/DeclarativeDurableFunctions.TestApp`](src/DeclarativeDurableFunctions.TestApp) for a complete working example.

## Status

**Alpha.** Core engine is complete with full test coverage. All eight step types are implemented: activity, sub-orchestration, foreach, parallel, wait-for-event, trigger-and-wait, poll, and switch. See [`docs/vision.md`](docs/vision.md) for the full design including schema reference, expression language, and engine architecture.

## Target platform

- Azure Functions — .NET isolated worker model
- C# first; Python and JS are future considerations
