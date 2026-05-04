using System.Text.Json.Serialization;

namespace DeclarativeDurableFunctions.TestApp.Models;

public class Order
{
    public string OrderId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    [JsonPropertyName("customerEmail")]  // exact match — used in {{input.customerEmail}}
    public string CustomerEmail { get; set; } = "";
    public string? CorrelationId { get; set; }
    public List<LineItem> LineItems { get; set; } = [];  // case-insensitive fallback — {{input.lineItems}}
}

public class LineItem
{
    [JsonPropertyName("lineItemId")]  // exact match — used in {{$item.lineItemId}}
    public string LineItemId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
}
