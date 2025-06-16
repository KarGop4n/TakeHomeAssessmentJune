using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class GulfSouthProviderTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<GulfSouthPipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        
        var provider = new GulfSouthPipelineDataProvider(httpClient, logger);
        
        System.Console.WriteLine("Testing Gulf South Pipeline Data Provider...");
        System.Console.WriteLine("===============================================");
        
        try
        {
            System.Console.WriteLine("Checking if Gulf South API is available...");
            var isAvailable = await provider.IsAvailableAsync();
            System.Console.WriteLine($"Gulf South Pipeline Available: {isAvailable}");
            
            if (!isAvailable)
            {
                System.Console.WriteLine("Cannot reach Gulf South API. Check your internet connection.");
                return;
            }
            
            System.Console.WriteLine("\nFetching recent notices from Gulf South API...");
            var notices = await provider.GetRecentNoticesAsync();
            
            System.Console.WriteLine($"Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("No notices found.");
                return;
            }
            
            System.Console.WriteLine("\nSample Notices:");
            System.Console.WriteLine("==================");
            
            foreach (var notice in notices.Take(8))
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
                
                var keyProperties = new[] { "EffectiveDate", "EndDate", "NoticeStatus", "Facility", "PipelineSegments", "MaintenanceTypes" };
                foreach (var key in keyProperties)
                {
                    if (notice.AdditionalProperties.ContainsKey(key))
                    {
                        var value = notice.AdditionalProperties[key];
                        var displayValue = value.Length > 60 ? value.Substring(0, 60) + "..." : value;
                        System.Console.WriteLine($"   {key}: {displayValue}");
                    }
                }
            }
            
            System.Console.WriteLine($"\nNotice Type Breakdown:");
            System.Console.WriteLine("=========================");
            
            var byType = notices.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            foreach (var group in byType)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byStatus = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("NoticeStatus", "Unknown"));
            System.Console.WriteLine($"\nBy Notice Status:");
            foreach (var group in byStatus)
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
            
            System.Console.WriteLine($"\nTrading Signal Quality Analysis:");
            System.Console.WriteLine("===================================");
            
            var tradingKeywords = new[] { 
                "maintenance", "outage", "compressor", "station", "capacity", 
                "reduction", "carthage", "index", "junction", "pipeline"
            };
            
            var keywordNotices = notices.Where(n => 
                tradingKeywords.Any(keyword => 
                    n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )).ToList();
            
            System.Console.WriteLine($"   Notices with trading keywords: {keywordNotices.Count}");
            System.Console.WriteLine($"   Notices with locations: {notices.Count(n => !string.IsNullOrEmpty(n.Location))}");
            System.Console.WriteLine($"   Notices with volume impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   Notices with effective dates: {notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate"))}");
            System.Console.WriteLine($"   Notices with pipeline segments: {notices.Count(n => n.AdditionalProperties.ContainsKey("PipelineSegments"))}");
            
            System.Console.WriteLine($"\nMost Interesting Notices for Trading:");
            System.Console.WriteLine("=========================================");
            
            var interestingNotices = notices
                .Where(n => 
                    n.NoticeType.Contains("Maintenance", StringComparison.OrdinalIgnoreCase) ||
                    n.Title.Contains("compressor", StringComparison.OrdinalIgnoreCase) ||
                    n.Title.Contains("outage", StringComparison.OrdinalIgnoreCase) ||
                    n.Title.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                    n.VolumeImpactMmbtu.HasValue
                )
                .OrderByDescending(n => n.NoticeDate)
                .Take(5);
            
            foreach (var notice in interestingNotices)
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
            
            System.Console.WriteLine($"\nProvider Success Metrics:");
            System.Console.WriteLine("============================");
            System.Console.WriteLine($"   API connectivity: Working (REST API)");
            System.Console.WriteLine($"   JSON parsing: {(notices.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   Notice ID extraction: {notices.Count(n => !string.IsNullOrEmpty(n.Id))} / {notices.Count}");
            System.Console.WriteLine($"   Date parsing: {notices.Count(n => n.NoticeDate > DateTime.MinValue)} / {notices.Count}");
            System.Console.WriteLine($"   Type extraction: {notices.Count(n => !string.IsNullOrEmpty(n.NoticeType))} / {notices.Count}");
            System.Console.WriteLine($"   Title extraction: {notices.Count(n => !string.IsNullOrEmpty(n.Title))} / {notices.Count}");
            
            var successRate = notices.Count > 0 ? (int)((notices.Count(n => 
                !string.IsNullOrEmpty(n.Id) && 
                !string.IsNullOrEmpty(n.Title) && 
                n.NoticeDate > DateTime.MinValue
            ) * 100.0) / notices.Count) : 0;
            
            System.Console.WriteLine($"   Overall success rate: {successRate}%");
            
            if (successRate > 80)
            {
                System.Console.WriteLine($"\nGulf South Provider is working excellently!");
                System.Console.WriteLine($"   - Modern REST API (fastest provider)");
                System.Console.WriteLine($"   - Extracted {notices.Count} notices");
                System.Console.WriteLine($"   - Clean JSON parsing");
                System.Console.WriteLine($"   - Ready for production use");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nGulf South provider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}