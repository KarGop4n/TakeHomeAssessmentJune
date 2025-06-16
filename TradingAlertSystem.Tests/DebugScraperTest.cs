using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class DebugScraperTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<AnrPipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        
        System.Console.WriteLine("🔍 Debug ANR HTML Content...");
        System.Console.WriteLine("=============================");
        
        try
        {
            // Test both Critical and NonCritical URLs
            var urls = new[]
            {
                "https://ebb.anrpl.com/Notices/Notices.asp?sPipelineCode=ANR&sSubCategory=Critical",
                "https://ebb.anrpl.com/Notices/Notices.asp?sPipelineCode=ANR&sSubCategory=NonCritical"
            };

            foreach (var url in urls)
            {
                System.Console.WriteLine($"\nFetching: {url}");
                
                var response = await httpClient.GetAsync(url);
                System.Console.WriteLine($"Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync();
                    
                    System.Console.WriteLine($"Content Length: {html.Length} characters");
                    System.Console.WriteLine($"Content Type: {response.Content.Headers.ContentType}");
                    
                    System.Console.WriteLine("\n--- First 500 Characters ---");
                    System.Console.WriteLine(html.Substring(0, Math.Min(500, html.Length)));
                    
                    System.Console.WriteLine("\n--- Table Analysis ---");
                    System.Console.WriteLine($"Contains '<table': {html.Contains("<table", StringComparison.OrdinalIgnoreCase)}");
                    System.Console.WriteLine($"Contains '<tbody': {html.Contains("<tbody", StringComparison.OrdinalIgnoreCase)}");
                    System.Console.WriteLine($"Contains '<tr': {html.Contains("<tr", StringComparison.OrdinalIgnoreCase)}");
                    System.Console.WriteLine($"Contains '<td': {html.Contains("<td", StringComparison.OrdinalIgnoreCase)}");
                    
                    var trCount = System.Text.RegularExpressions.Regex.Matches(html, "<tr", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                    var tdCount = System.Text.RegularExpressions.Regex.Matches(html, "<td", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                    System.Console.WriteLine($"<tr> count: {trCount}");
                    System.Console.WriteLine($"<td> count: {tdCount}");
                    
                    var keywords = new[] { "Force Maj", "Plnd Outage", "Critical", "CAPACITY", "Notice" };
                    System.Console.WriteLine("\n--- Content Keywords ---");
                    foreach (var keyword in keywords)
                    {
                        var count = System.Text.RegularExpressions.Regex.Matches(html, keyword, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
                        System.Console.WriteLine($"'{keyword}': {count} occurrences");
                    }
                    
                    System.Console.WriteLine("\n--- Testing Selectors ---");
                    var doc = new HtmlAgilityPack.HtmlDocument();
                    doc.LoadHtml(html);
                    
                    var selectors = new[]
                    {
                        "//table//tbody/tr",
                        "//table//tr",
                        "//tbody/tr", 
                        "//tr",
                        "//table",
                        "//tbody"
                    };
                    
                    foreach (var selector in selectors)
                    {
                        var nodes = doc.DocumentNode.SelectNodes(selector);
                        System.Console.WriteLine($"Selector '{selector}': {nodes?.Count ?? 0} nodes found");
                    }
                    
                    var firstRow = doc.DocumentNode.SelectSingleNode("//tr");
                    if (firstRow != null)
                    {
                        System.Console.WriteLine("\n--- First Table Row ---");
                        System.Console.WriteLine(firstRow.OuterHtml.Substring(0, Math.Min(300, firstRow.OuterHtml.Length)));
                    }
                }
                
                System.Console.WriteLine("\n" + new string('=', 50));
                
                await Task.Delay(2000);
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nDebug complete. Press any key to exit...");
        System.Console.ReadKey();
    }
}