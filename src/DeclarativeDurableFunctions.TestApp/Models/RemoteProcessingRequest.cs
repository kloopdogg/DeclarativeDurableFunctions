namespace DeclarativeDurableFunctions.TestApp.Models;

public class RemoteProcessingRequest
{
    public string OrderId { get; set; } = string.Empty;
    public string CallbackInstanceId { get; set; } = string.Empty;
}