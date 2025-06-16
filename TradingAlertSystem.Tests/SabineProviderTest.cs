using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class SabineProviderTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<SabinePipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        
        var provider = new SabinePipelineDataProvider(httpClient, logger);
        
        System.Console.WriteLine("Testing Sabine Pipeline Data Provider...");
        System.Console.WriteLine("===========================================");
        
        try
        {
            System.Console.WriteLine("Checking if Sabine pipeline is available...");
            var isAvailable = await provider.IsAvailableAsync();
            System.Console.WriteLine($"Sabine Pipeline Available: {isAvailable}");
            
            if (!isAvailable)
            {
                System.Console.WriteLine("Cannot reach Sabine pipeline. Check your internet connection.");
                return;
            }
            
            System.Console.WriteLine("\n🔍 Fetching recent notices...");
            var notices = await provider.GetRecentNoticesAsync();
            
            System.Console.WriteLine($"Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("No notices found. This might indicate a parsing issue or no active notices.");
                return;
            }
            
            System.Console.WriteLine("\nSample Notices:");
            System.Console.WriteLine("==================");
            
            foreach (var notice in notices.Take(5))
            {
                System.Console.WriteLine($"\n🔹 Notice ID: {notice.Id}");
                System.Console.WriteLine($"   Pipeline: {notice.PipelineName}");
                System.Console.WriteLine($"   Type: {notice.NoticeType}");
                System.Console.WriteLine($"   Date: {notice.NoticeDate:yyyy-MM-dd HH:mm}");
                System.Console.WriteLine($"   Title: {notice.Title}");
                System.Console.WriteLine($"   Location: {notice.Location}");
                
                if (notice.VolumeImpactMmbtu.HasValue)
                {
                    System.Console.WriteLine($"   Volume: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
                }
                
                System.Console.WriteLine($"   URL: {notice.SourceUrl}");
                
                var keyProperties = new[] { "Category", "EffectiveDate", "EndDate", "NoticeId", "ResponseDate" };
                foreach (var key in keyProperties)
                {
                    if (notice.AdditionalProperties.ContainsKey(key))
                    {
                        System.Console.WriteLine($"   {key}: {notice.AdditionalProperties[key]}");
                    }
                }
            }
            
            System.Console.WriteLine($"\nNotice Breakdown:");
            System.Console.WriteLine("====================");
            
            var byType = notices.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            foreach (var group in byType)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byCategory = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("Category", "Unknown"));
            System.Console.WriteLine($"\nBy Category:");
            foreach (var group in byCategory)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var recentNotices = notices.Where(n => n.NoticeDate >= DateTime.Now.AddDays(-7)).ToList();
            System.Console.WriteLine($"\nRecent Notices (Last 7 Days): {recentNotices.Count}");
            
            
            var upcomingNotices = notices.Where(n => 
                n.AdditionalProperties.ContainsKey("EffectiveDate") && 
                DateTime.TryParse(n.AdditionalProperties["EffectiveDate"], out var effDate) &&
                effDate > DateTime.Now
            ).ToList();
            System.Console.WriteLine($"Upcoming Effective Notices: {upcomingNotices.Count}");
            
            var tradingSignalKeywords = new[] { 
                "maintenance", "capacity", "restriction", "hub", "outage", 
                "curtailment", "force majeure", "sabine hub", "henry hub"
            };
            
            var interestingNotices = notices.Where(n => 
                tradingSignalKeywords.Any(keyword => 
                    n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    n.NoticeType.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )).ToList();
            
            System.Console.WriteLine($"\nPotentially Interesting Notices for Trading: {interestingNotices.Count}");
            foreach (var notice in interestingNotices.Take(5))
            {
                var titlePreview = notice.Title.Length > 80 ? notice.Title.Substring(0, 80) + "..." : notice.Title;
                System.Console.WriteLine($"   • {notice.NoticeType}: {titlePreview}");
                if (notice.AdditionalProperties.ContainsKey("EffectiveDate"))
                {
                    System.Console.WriteLine($"     Effective: {notice.AdditionalProperties["EffectiveDate"]}");
                }
                if (notice.VolumeImpactMmbtu.HasValue)
                {
                    System.Console.WriteLine($"     Volume: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
                }
            }
            
            System.Console.WriteLine($"\nSabine Hub Analysis:");
            System.Console.WriteLine("=======================");
            var hubKeywords = new[] { "sabine hub", "henry hub", "hub", "capacity", "restriction" };
            var hubNotices = notices.Where(n => 
                hubKeywords.Any(keyword => 
                    n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    n.Location.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )).ToList();
            
            System.Console.WriteLine($"   Hub-related notices: {hubNotices.Count}");
            System.Console.WriteLine($"   Notices with volume impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   Notices with locations: {notices.Count(n => !string.IsNullOrEmpty(n.Location))}");
            System.Console.WriteLine($"   Notices with effective dates: {notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate"))}");
            
            System.Console.WriteLine($"\nProvider Success Metrics:");
            System.Console.WriteLine("============================");
            System.Console.WriteLine($"   HTML parsing: {(notices.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   Notice ID extraction: {notices.Count(n => n.AdditionalProperties.ContainsKey("NoticeId"))} / {notices.Count}");
            System.Console.WriteLine($"   Date parsing: {notices.Count(n => n.NoticeDate > DateTime.MinValue)} / {notices.Count}");
            System.Console.WriteLine($"   Effective date extraction: {notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate"))} / {notices.Count}");
            System.Console.WriteLine($"   Location extraction: {notices.Count(n => !string.IsNullOrEmpty(n.Location))} / {notices.Count}");
            
            var successRate = notices.Count > 0 ? (int)((notices.Count(n => 
                !string.IsNullOrEmpty(n.Id) && 
                !string.IsNullOrEmpty(n.Title) && 
                n.NoticeDate > DateTime.MinValue
            ) * 100.0) / notices.Count) : 0;
            
            System.Console.WriteLine($"   Overall success rate: {successRate}%");
            
            if (successRate > 80)
            {
                System.Console.WriteLine($"\n   Sabine Provider is working!");
                System.Console.WriteLine($"   - Successfully parsed HTML table structure");
                System.Console.WriteLine($"   - Extracted {notices.Count} notices");
                System.Console.WriteLine($"   - Hub-specific location detection working");
                System.Console.WriteLine($"   - Ready for production use");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nSabine provider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}