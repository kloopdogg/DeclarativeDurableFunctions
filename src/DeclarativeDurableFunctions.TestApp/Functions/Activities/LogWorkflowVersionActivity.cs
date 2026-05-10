using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public partial class LogWorkflowVersionActivity(ILogger<LogWorkflowVersionActivity> logger)
{
    [Function("LogWorkflowVersionActivity")]
    public object LogVersion([ActivityTrigger] LogVersionRequest request)
    {
        LogVersionMessage(request.WorkflowName, request.Version, request.InstanceId, request.ParentInstanceId);
        return new { logged = true };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Workflow version check: {WorkflowName} v{Version} | instanceId={InstanceId} | parentInstanceId={ParentInstanceId}")]
    private partial void LogVersionMessage(string workflowName, string version, string instanceId, string? parentInstanceId);
}

public class LogVersionRequest
{
    public string WorkflowName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? ParentInstanceId { get; set; }
}
