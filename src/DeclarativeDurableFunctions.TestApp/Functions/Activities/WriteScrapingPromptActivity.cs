using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DeclarativeDurableFunctions.TestApp.Functions.Activities;

public class WriteScrapingPromptActivity
{
    [Function("WriteScrapingPromptActivity")]
    public object RunAsync(
        [ActivityTrigger] JsonElement input,
        FunctionContext context)
    {
        ILogger logger = context.GetLogger(nameof(WriteScrapingPromptActivity));
        string siteUrl = input.GetProperty("siteUrl").GetString()!;
        string prompt = input.GetProperty("prompt").GetString()!;
 
        logger.LogWarning("Writing scraping prompt for spa site: {Site} and prompt: {Prompt}", siteUrl, prompt);
        
        return new
        {
            scrapingPrompt = "Extract the full schedule for the Jacksonville Jr. Icemen 16U AA team ('Icemen') from this page."
        };
    }
}
