using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class ColumbiaCsvTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<ColumbiaGulfCsvProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        httpClient.Timeout = TimeSpan.FromMinutes(3);
        
        var provider = new ColumbiaGulfCsvProvider(httpClient, logger);
        
        System.Console.WriteLine("Testing Columbia Gulf Enhanced CSV Provider...");
        System.Console.WriteLine("==================================================");
        System.Console.WriteLine("Now fetching complete dataset (7,579+ notices instead of 180)");
        System.Console.WriteLine("Includes all Critical, NonCritical, and Planned Maintenance notices");        
        try
        {
            System.Console.WriteLine("📡 Checking if Columbia Gulf CSV endpoint is available...");
            var isAvailable = await provider.IsAvailableAsync();
            System.Console.WriteLine($"Columbia Gulf CSV Available: {isAvailable}");
            
            if (!isAvailable)
            {
                System.Console.WriteLine("Cannot reach Columbia Gulf CSV endpoint. Check your internet connection.");
                return;
            }
            
            System.Console.WriteLine("\nFetching recent notices via CSV...");
            var notices = await provider.GetRecentNoticesAsync();
            
            System.Console.WriteLine($"Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("No notices found.");
                return;
            }
            
            System.Console.WriteLine("Sample Notices:");
            System.Console.WriteLine("==================");
            
            foreach (var notice in notices.Take(5)) // Show first 5 notices
            {
                System.Console.WriteLine($"\n🔹 Notice ID: {notice.Id}");
                System.Console.WriteLine($"   Pipeline: {notice.PipelineName}");
                System.Console.WriteLine($"   Type: {notice.NoticeType}");
                System.Console.WriteLine($"   Post Date: {notice.NoticeDate:yyyy-MM-dd HH:mm}");
                System.Console.WriteLine($"   Title: {notice.Title}");
                System.Console.WriteLine($"   Location: {notice.Location}");
                
                if (notice.VolumeImpactMmbtu.HasValue)
                {
                    System.Console.WriteLine($"   Volume: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
                }
                
                System.Console.WriteLine($"   URL: {notice.SourceUrl}");
                
                if (notice.AdditionalProperties.Any())
                {
                    System.Console.WriteLine($"   Additional Data:");
                    foreach (var prop in notice.AdditionalProperties.Take(5)) // Show first 5 properties
                    {
                        var value = prop.Value.Length > 50 ? prop.Value.Substring(0, 50) + "..." : prop.Value;
                        System.Console.WriteLine($"     {prop.Key}: {value}");
                    }
                }
            }
            
            System.Console.WriteLine($"\nNotice Breakdown:");
            System.Console.WriteLine("=============================");
            
            var byType = notices.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            foreach (var group in byType.Take(15)) // Show top 15 notice types
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byCategory = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("Category", "Unknown"));
            System.Console.WriteLine($"\n📂 By Critical Status:");
            foreach (var group in byCategory)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var plannedMaintenance = notices.Where(n => n.AdditionalProperties.ContainsKey("IsPlannedMaintenance")).ToList();
            System.Console.WriteLine($"\n🔧 Planned Maintenance Analysis:");
            System.Console.WriteLine($"   Total Planned Maintenance: {plannedMaintenance.Count} notices");
            
            var maintenanceByType = plannedMaintenance.GroupBy(n => n.NoticeType).OrderByDescending(g => g.Count());
            foreach (var group in maintenanceByType.Take(10))
            {
                System.Console.WriteLine($"     {group.Key}: {group.Count()} notices");
            }
            
            var recentNotices = notices.Where(n => n.NoticeDate >= DateTime.Now.AddDays(-7)).ToList();
            System.Console.WriteLine($"\n📅 Recent Notices (Last 7 Days): {recentNotices.Count}");
            
            var recentMaintenance = recentNotices.Where(n => n.AdditionalProperties.ContainsKey("IsPlannedMaintenance")).Count();
            System.Console.WriteLine($"📅 Recent Planned Maintenance: {recentMaintenance}");
            
            var withLocations = notices.Where(n => !string.IsNullOrEmpty(n.Location)).ToList();
            System.Console.WriteLine($"\n📍 Location Analysis:");
            System.Console.WriteLine($"   Notices with locations: {withLocations.Count}");
            
            var locationBreakdown = withLocations.GroupBy(n => n.Location).OrderByDescending(g => g.Count()).Take(10);
            foreach (var group in locationBreakdown)
            {
                System.Console.WriteLine($"     {group.Key}: {group.Count()} notices");
            }
            
            var withFacilities = notices.Where(n => n.AdditionalProperties.ContainsKey("Facility")).ToList();
            System.Console.WriteLine($"\n🏭 Facility Analysis:");
            System.Console.WriteLine($"   Notices with facility info: {withFacilities.Count}");
            
            var facilityBreakdown = withFacilities.GroupBy(n => n.AdditionalProperties["Facility"]).OrderByDescending(g => g.Count()).Take(8);
            foreach (var group in facilityBreakdown)
            {
                System.Console.WriteLine($"     {group.Key}: {group.Count()} notices");
            }
            
            var tradingSignalKeywords = new[] { 
                "capacity", "tim", "maintenance", "outage", "force majeure", 
                "curtailment", "constraint", "louisiana", "henry hub", "reduction",
                "pigging", "compressor", "construction", "line", "planned"
            };
            
            var interestingNotices = notices.Where(n => 
                tradingSignalKeywords.Any(keyword => 
                    n.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    n.NoticeType.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                )).ToList();
            
            System.Console.WriteLine($"\n🚨 Potentially Interesting Trading Notices: {interestingNotices.Count}");
            
            var forceMajeure = interestingNotices.Count(n => n.Title.Contains("force majeure", StringComparison.OrdinalIgnoreCase));
            var capacity = interestingNotices.Count(n => n.Title.Contains("capacity", StringComparison.OrdinalIgnoreCase));
            var maintenance = interestingNotices.Count(n => n.Title.Contains("maintenance", StringComparison.OrdinalIgnoreCase));
            var outages = interestingNotices.Count(n => n.Title.Contains("outage", StringComparison.OrdinalIgnoreCase));
            
            System.Console.WriteLine($"   Force Majeure: {forceMajeure}");
            System.Console.WriteLine($"   Capacity-related: {capacity}");
            System.Console.WriteLine($"   Maintenance-related: {maintenance}");
            System.Console.WriteLine($"   Outage-related: {outages}");
            
            System.Console.WriteLine($"\n📋 Sample High-Impact Notices:");
            var highImpactNotices = interestingNotices
                .Where(n => n.Title.Contains("force majeure", StringComparison.OrdinalIgnoreCase) ||
                           n.Title.Contains("compressor", StringComparison.OrdinalIgnoreCase) ||
                           n.Title.Contains("line", StringComparison.OrdinalIgnoreCase) ||
                           n.AdditionalProperties.GetValueOrDefault("Critical") == "Y")
                .Take(8);
                
            foreach (var notice in highImpactNotices)
            {
                var titlePreview = notice.Title.Length > 90 ? notice.Title.Substring(0, 90) + "..." : notice.Title;
                System.Console.WriteLine($"   • {notice.NoticeType}: {titlePreview}");
                if (notice.AdditionalProperties.ContainsKey("Critical"))
                {
                    System.Console.WriteLine($"     Critical: {notice.AdditionalProperties["Critical"]} | Location: {notice.Location}");
                }
                if (notice.AdditionalProperties.ContainsKey("EffectiveDate"))
                {
                    System.Console.WriteLine($"     Effective: {notice.AdditionalProperties["EffectiveDate"]}");
                }
            }
            
            System.Console.WriteLine($"\n🔧 Enhanced CSV Data Quality:");
            System.Console.WriteLine("=============================");
            var withNoticeIds = notices.Count(n => n.AdditionalProperties.ContainsKey("NoticeId"));
            var withEffectiveDates = notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate"));
            var withEndDates = notices.Count(n => n.AdditionalProperties.ContainsKey("EndDate"));
            var withTspNames = notices.Count(n => n.AdditionalProperties.ContainsKey("TSPName"));
            var withCriticalFlags = notices.Count(n => n.AdditionalProperties.ContainsKey("Critical"));
            var withPlannedMaintenance = notices.Count(n => n.AdditionalProperties.ContainsKey("IsPlannedMaintenance"));
            var withFacilityInfo = notices.Count(n => n.AdditionalProperties.ContainsKey("Facility"));
            
            System.Console.WriteLine($"   Total notices: {notices.Count}");
            System.Console.WriteLine($"   With Notice IDs: {withNoticeIds}");
            System.Console.WriteLine($"   With Effective Dates: {withEffectiveDates}");
            System.Console.WriteLine($"   With End Dates: {withEndDates}");
            System.Console.WriteLine($"   With TSP Names: {withTspNames}");
            System.Console.WriteLine($"   With Critical Flags: {withCriticalFlags}");
            System.Console.WriteLine($"   With Volume Impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   With Location Data: {notices.Count(n => !string.IsNullOrEmpty(n.Location))}");
            System.Console.WriteLine($"   Planned Maintenance Notices: {withPlannedMaintenance}");
            System.Console.WriteLine($"   With Facility Information: {withFacilityInfo}");
            
            var completenessScore = (int)((withNoticeIds + withEffectiveDates + withTspNames + withCriticalFlags) * 100.0 / (notices.Count * 4));
            System.Console.WriteLine($"   Data Completeness Score: {completenessScore}%");
            
            System.Console.WriteLine($"\nEnhancement vs Original Approach:");
            System.Console.WriteLine($"   Original dataset: 180 notices (119 Critical + 61 NonCritical)");
            System.Console.WriteLine($"   Enhanced dataset: {notices.Count} notices ({notices.Count - 180:+0} additional)");
            System.Console.WriteLine($"   Improvement: {((notices.Count - 180.0) / 180.0 * 100):F1}% more data");
            System.Console.WriteLine($"   Maintenance coverage: {withPlannedMaintenance} planned maintenance notices");
            System.Console.WriteLine($"   Enhanced location extraction: {notices.Count(n => !string.IsNullOrEmpty(n.Location))} notices with locations");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nCSV provider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}