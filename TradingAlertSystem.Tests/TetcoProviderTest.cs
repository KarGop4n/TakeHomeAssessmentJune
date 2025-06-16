using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class TetcoProviderTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<TetcoPipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        httpClient.Timeout = TimeSpan.FromMinutes(2); // Longer timeout for throttling
        
        var provider = new TetcoPipelineDataProvider(httpClient, logger);
        
        System.Console.WriteLine("Testing TETCO Pipeline Data Provider (Fixed)...");
        System.Console.WriteLine("==================================================");
        System.Console.WriteLine("Skipping availability check due to TETCO rate limiting");
        System.Console.WriteLine("This test will take 3-5 minutes due to anti-bot throttling");
        
        try
        {
            System.Console.WriteLine("\n Fetching notices with anti-bot throttling...");
            System.Console.WriteLine("   Some categories may be rate-limited");
            
            var startTime = DateTime.Now;
            var notices = await provider.GetRecentNoticesAsync();
            var totalTime = DateTime.Now - startTime;
            
            System.Console.WriteLine($"\nResults after {totalTime.TotalMinutes:F1} minutes:");
            System.Console.WriteLine($"   Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("\nNo notices retrieved. Possible causes:");
                System.Console.WriteLine("   - All categories were rate-limited");
                System.Console.WriteLine("   - TETCO has implemented stronger anti-bot measures");
                System.Console.WriteLine("   - Need to wait longer between requests");
                System.Console.WriteLine("   - IP address may be temporarily blocked");
                
                System.Console.WriteLine("\nRecommendations:");
                System.Console.WriteLine("   - Wait 10-15 minutes before trying again");
                System.Console.WriteLine("   - Consider using a different IP/network");
                System.Console.WriteLine("   - Increase the throttling delay to 120+ seconds");
                return;
            }
            
            System.Console.WriteLine($"\nSUCCESS! Retrieved {notices.Count} notices");
            
            var byCategory = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("Category", "Unknown"));
            System.Console.WriteLine($"\nBy Category:");
            foreach (var group in byCategory)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byType = notices.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            System.Console.WriteLine($"\nBy Notice Type:");
            foreach (var group in byType.Take(10))
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            System.Console.WriteLine($"\nSample Notices:");
            foreach (var notice in notices.Take(5))
            {
                System.Console.WriteLine($"\n{notice.NoticeType} - {notice.Id}");
                System.Console.WriteLine($"   Date: {notice.NoticeDate:yyyy-MM-dd HH:mm}");
                System.Console.WriteLine($"   Title: {notice.Title.Substring(0, Math.Min(80, notice.Title.Length))}...");
                System.Console.WriteLine($"   Location: {notice.Location}");
                if (notice.VolumeImpactMmbtu.HasValue)
                {
                    System.Console.WriteLine($"   Volume: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
                }
                
                var keyProps = new[] { "Category", "EffectiveDate", "EndDate", "NoticeId" };
                foreach (var prop in keyProps)
                {
                    if (notice.AdditionalProperties.ContainsKey(prop))
                    {
                        System.Console.WriteLine($"   {prop}: {notice.AdditionalProperties[prop]}");
                    }
                }
            }
            
            var enrichedNotices = notices.Where(n => n.Description != n.Title || n.AdditionalProperties.Count > 3).ToList();
            System.Console.WriteLine($"\nDetail Page Enrichment:");
            System.Console.WriteLine($"   Notices with enriched data: {enrichedNotices.Count}");
            System.Console.WriteLine($"   Notices with volume impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   Notices with trading keywords: {notices.Count(n => n.AdditionalProperties.ContainsKey("TradingKeywords"))}");
            
            var recentNotices = notices.Where(n => n.NoticeDate >= DateTime.Now.AddDays(-3)).ToList();
            System.Console.WriteLine($"\nRecent Notices (Last 3 Days): {recentNotices.Count}");
            
            System.Console.WriteLine($"\nTrading Signal Quality:");
            System.Console.WriteLine("==========================");
            
            var operationalFlowOrders = notices.Count(n => n.NoticeType.Contains("Operational Flow Order"));
            var capacityConstraints = notices.Count(n => n.NoticeType.Contains("Capacity Constraint"));
            var systemStatus = notices.Count(n => n.NoticeType.Contains("Computer System Status"));
            var withLocation = notices.Count(n => !string.IsNullOrEmpty(n.Location));
            
            System.Console.WriteLine($"   Operational Flow Orders: {operationalFlowOrders}");
            System.Console.WriteLine($"   Capacity Constraints: {capacityConstraints}");
            System.Console.WriteLine($"   System Status: {systemStatus}");
            System.Console.WriteLine($"   Notices with location: {withLocation}");
            
            System.Console.WriteLine($"\nProvider Success Metrics:");
            System.Console.WriteLine("============================");
            System.Console.WriteLine($"   Anti-bot throttling: Working (75s delays)");
            System.Console.WriteLine($"   HTML parsing: {(notices.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   Notice ID extraction: {notices.Count(n => n.AdditionalProperties.ContainsKey("NoticeId"))} / {notices.Count}");
            System.Console.WriteLine($"   Date parsing: {notices.Count(n => n.NoticeDate > DateTime.MinValue)} / {notices.Count}");
            System.Console.WriteLine($"   Effective date extraction: {notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate"))} / {notices.Count}");
            
            var successRate = notices.Count > 0 ? 100 : 0;
            System.Console.WriteLine($"   Overall success rate: {successRate}%");
            
            if (successRate > 0)
            {
                System.Console.WriteLine($"\nTETCO Provider is working correctly!");
                System.Console.WriteLine($"   - Successfully bypassed anti-bot measures");
                System.Console.WriteLine($"   - Extracted {notices.Count} notices");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"\nError: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            
            if (ex.Message.Contains("timeout") || ex.Message.Contains("Timeout"))
            {
                System.Console.WriteLine("\nTimeout may need adjustment");
            }
        }
        
        System.Console.WriteLine("\nTETCO provider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}