using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class CreoleTrailProviderTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<CreoleTrailPipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        httpClient.DefaultRequestHeaders.Add("Referer", "https://lngconnection.cheniere.com/");
        httpClient.DefaultRequestHeaders.Add("Origin", "https://lngconnection.cheniere.com");
        
        var provider = new CreoleTrailPipelineDataProvider(httpClient, logger);
        
        System.Console.WriteLine("Testing Creole Trail Pipeline Data Provider...");
        System.Console.WriteLine("================================================");
        
        try
        {
            System.Console.WriteLine("Checking if Creole Trail API is available...");
            var isAvailable = await provider.IsAvailableAsync();
            System.Console.WriteLine($"Creole Trail Pipeline Available: {isAvailable}");
            
            if (!isAvailable)
            {
                System.Console.WriteLine("Cannot reach Creole Trail API. Check your internet connection.");
                return;
            }
            
            System.Console.WriteLine("\nFetching recent notices from both pipelines...");
            var notices = await provider.GetRecentNoticesAsync();
            
            System.Console.WriteLine($"Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("⚠️  No notices found.");
                return;
            }
            
            System.Console.WriteLine("\nSample Notices:");
            System.Console.WriteLine("==================");
            
            foreach (var notice in notices.Take(8))  // Display first 8 notices
            {
                System.Console.WriteLine($"\nNotice ID: {notice.Id}");
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
                
                var keyProperties = new[] { "Category", "NoticeStatus", "EffectiveDate", "EndDate" };
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
            
            var byPipeline = notices.GroupBy(n => n.PipelineName).OrderByDescending(g => g.Count());
            foreach (var group in byPipeline)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byType = notices.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            System.Console.WriteLine($"\nBy Notice Type:");
            foreach (var group in byType.Take(8))
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
            
            var tradingSignalKeywords = new[] { 
                "outage", "maintenance", "unscheduled", "capacity", "reduction", 
                "force majeure", "terminal", "compressor", "planned", "emergency"
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
                System.Console.WriteLine($"   • {notice.PipelineName} - {notice.NoticeType}: {titlePreview}");
                if (notice.AdditionalProperties.ContainsKey("NoticeStatus"))
                {
                    System.Console.WriteLine($"     Status: {notice.AdditionalProperties["NoticeStatus"]}");
                }
            }
            
            // LNG-specific analysis
            System.Console.WriteLine($"\nLNG Terminal Analysis:");
            System.Console.WriteLine("=========================");
            var lngKeywords = new[] { "terminal", "lng", "sabine", "corpus christi", "cameron", "compressor" };
            var lngNotices = notices.Where(n => 
                lngKeywords.Any(keyword => 
                    n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    n.Location.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )).ToList();
            
            System.Console.WriteLine($"   LNG Terminal-related notices: {lngNotices.Count}");
            System.Console.WriteLine($"   Notices with volume impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   Notices with locations: {notices.Count(n => !string.IsNullOrEmpty(n.Location))}");
            
            var statusBreakdown = notices
                .Where(n => n.AdditionalProperties.ContainsKey("NoticeStatus"))
                .GroupBy(n => n.AdditionalProperties["NoticeStatus"])
                .OrderByDescending(g => g.Count());
            
            System.Console.WriteLine($"\nNotice Status Breakdown:");
            foreach (var group in statusBreakdown)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nProvider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}