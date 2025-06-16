using Microsoft.Extensions.Logging;
using TradingAlertSystem.Infrastructure.Providers;

namespace TradingAlertSystem.Console;

public class NGPLProviderTest
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        
        var logger = loggerFactory.CreateLogger<NGPLPipelineDataProvider>();
        
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        
        var provider = new NGPLPipelineDataProvider(httpClient, logger);
        
        System.Console.WriteLine("🧪 Testing NGPL Pipeline Data Provider...");
        System.Console.WriteLine("=====================================================================");
        
        try
        {
            System.Console.WriteLine("📡 Checking if NGPL pipeline is available...");
            var isAvailable = await provider.IsAvailableAsync();
            System.Console.WriteLine($"NGPL Pipeline Available: {isAvailable}");
            
            if (!isAvailable)
            {
                System.Console.WriteLine("Cannot reach NGPL pipeline. Check your internet connection.");
                return;
            }
            
            
            System.Console.WriteLine("\nFetching notices from all categories (including PDF parsing)...");
            System.Console.WriteLine("Note: PDF parsing may take 30-60 seconds...");
            var notices = await provider.GetRecentNoticesAsync();
            
            System.Console.WriteLine($"Total notices retrieved: {notices.Count}");
            
            if (notices.Count == 0)
            {
                System.Console.WriteLine("No notices found. This might indicate a parsing issue.");
                return;
            }
            
            var regularNotices = notices.Where(n => n.AdditionalProperties.GetValueOrDefault("Category") != "PlannedServiceOutages").ToList();
            var outageNotices = notices.Where(n => n.AdditionalProperties.GetValueOrDefault("Category") == "PlannedServiceOutages").ToList();
            
            System.Console.WriteLine($"Regular Notices: {regularNotices.Count}");
            System.Console.WriteLine($"PDF-Parsed Outage Notices: {outageNotices.Count}");
            
            if (regularNotices.Any())
            {
                System.Console.WriteLine("\nSample Regular Notices:");
                System.Console.WriteLine("=========================");
                
                foreach (var notice in regularNotices.Take(5))
                {
                    System.Console.WriteLine($"\nNotice ID: {notice.Id}");
                    System.Console.WriteLine($"   Type: {notice.NoticeType}");
                    System.Console.WriteLine($"   Date: {notice.NoticeDate:yyyy-MM-dd HH:mm}");
                    System.Console.WriteLine($"   Title: {notice.Title}");
                    System.Console.WriteLine($"   Location: {notice.Location}");
                    
                    if (notice.VolumeImpactMmbtu.HasValue)
                    {
                        System.Console.WriteLine($"   Volume: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
                    }
                    
                    var keyProperties = new[] { "Category", "TradingPriority", "ConstraintType", "NoticeAction" };
                    foreach (var key in keyProperties)
                    {
                        if (notice.AdditionalProperties.ContainsKey(key))
                        {
                            System.Console.WriteLine($"   {key}: {notice.AdditionalProperties[key]}");
                        }
                    }
                }
            }
            
            if (outageNotices.Any())
            {
                System.Console.WriteLine("\nPDF-Parsed Planned Service Outages:");
                System.Console.WriteLine("======================================");
                
                var summaryNotices = outageNotices.Where(n => n.NoticeType.Contains("SUMMARY")).ToList();
                var capacityNotices = outageNotices.Where(n => n.NoticeType.Contains("CAPACITY RESTRICTION")).ToList();
                
                foreach (var summary in summaryNotices)
                {
                    System.Console.WriteLine($"\n{summary.Title}");
                    System.Console.WriteLine($"   Description: {summary.Description}");
                    System.Console.WriteLine($"   Total Restrictions: {summary.AdditionalProperties.GetValueOrDefault("TotalRestrictions")}");
                    System.Console.WriteLine($"   Significant Restrictions: {summary.AdditionalProperties.GetValueOrDefault("SignificantRestrictions")}");
                    System.Console.WriteLine($"   Major Restrictions: {summary.AdditionalProperties.GetValueOrDefault("MajorRestrictions")}");
                    System.Console.WriteLine($"   Trading Priority: {summary.AdditionalProperties.GetValueOrDefault("TradingPriority")}");
                }
                
                System.Console.WriteLine($"\nIndividual Capacity Restrictions:");
                foreach (var restriction in capacityNotices)
                {
                    var severity = restriction.AdditionalProperties.GetValueOrDefault("TradingPriority") switch
                    {
                        "CRITICAL" => "🔴",
                        "HIGH" => "🟠", 
                        "MEDIUM" => "🟡",
                        _ => "🟢"
                    };
                    
                    System.Console.WriteLine($"\n{severity} {restriction.Title}");
                    System.Console.WriteLine($"   Station: {restriction.AdditionalProperties.GetValueOrDefault("Station")}");
                    System.Console.WriteLine($"   Capacity: {restriction.AdditionalProperties.GetValueOrDefault("CapacityPercentage")}%");
                    System.Console.WriteLine($"   Description: {restriction.AdditionalProperties.GetValueOrDefault("OutageDescription")}");
                    System.Console.WriteLine($"   Work Order: {restriction.AdditionalProperties.GetValueOrDefault("WorkOrderNumber")}");
                    System.Console.WriteLine($"   Start Date: {restriction.AdditionalProperties.GetValueOrDefault("OutageStartDate")}");
                    System.Console.WriteLine($"   End Date: {restriction.AdditionalProperties.GetValueOrDefault("OutageEndDate")}");
                    System.Console.WriteLine($"   Trading Priority: {restriction.AdditionalProperties.GetValueOrDefault("TradingPriority")}");
                    System.Console.WriteLine($"   Restriction Type: {restriction.AdditionalProperties.GetValueOrDefault("RestrictionType")}");
                    
                    if (restriction.VolumeImpactMmbtu.HasValue)
                    {
                        System.Console.WriteLine($"   Estimated Volume Impact: {restriction.VolumeImpactMmbtu:N0} {restriction.VolumeUnit}");
                    }
                }
            }
            
            
            System.Console.WriteLine($"\nNotice Breakdown:");
            System.Console.WriteLine("====================");
            
            var byCategory = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("Category", "Unknown"));
            foreach (var group in byCategory)
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var byPriority = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("TradingPriority", "Unknown"));
            System.Console.WriteLine($"\nBy Trading Priority:");
            foreach (var group in byPriority.OrderByDescending(g => g.Key))
            {
                System.Console.WriteLine($"   {group.Key}: {group.Count()} notices");
            }
            
            var recentNotices = notices.Where(n => n.NoticeDate >= DateTime.Now.AddDays(-7)).ToList();
            System.Console.WriteLine($"\nRecent Notices (Last 7 Days): {recentNotices.Count}");
            
            System.Console.WriteLine($"\nPDF Processing Analysis:");
            System.Console.WriteLine("===========================");
            
            var pdfNotices = notices.Where(n => n.SourceUrl.Contains(".pdf")).ToList();
            var capacityRestrictions = notices.Where(n => n.NoticeType.Contains("CAPACITY RESTRICTION")).ToList();
            
            System.Console.WriteLine($"   PDF-sourced notices: {pdfNotices.Count}");
            System.Console.WriteLine($"   Capacity restrictions extracted: {capacityRestrictions.Count}");
            System.Console.WriteLine($"   High/Critical priority restrictions: {capacityRestrictions.Count(n => n.AdditionalProperties.GetValueOrDefault("TradingPriority") is "HIGH" or "CRITICAL")}");
            System.Console.WriteLine($"   Restrictions with work orders: {capacityRestrictions.Count(n => !string.IsNullOrEmpty(n.AdditionalProperties.GetValueOrDefault("WorkOrderNumber")))}");
            System.Console.WriteLine($"   Restrictions with date ranges: {capacityRestrictions.Count(n => !string.IsNullOrEmpty(n.AdditionalProperties.GetValueOrDefault("OutageStartDate")))}");
            
            System.Console.WriteLine($"\nTrading Signal Quality:");
            System.Console.WriteLine("===========================");
            
            var highValueSignals = notices.Where(n => 
                n.AdditionalProperties.GetValueOrDefault("TradingPriority") is "HIGH" or "CRITICAL" ||
                n.NoticeType.Contains("CAPACITY CONSTRAINT") ||
                n.NoticeType.Contains("MAINTENANCE") ||
                (n.AdditionalProperties.ContainsKey("CapacityPercentage") && 
                 int.TryParse(n.AdditionalProperties["CapacityPercentage"], out var cap) && cap < 90)
            ).ToList();
            
            System.Console.WriteLine($"   High-value trading signals: {highValueSignals.Count}");
            System.Console.WriteLine($"   Notices with locations: {notices.Count(n => !string.IsNullOrEmpty(n.Location))}");
            System.Console.WriteLine($"   Notices with volume impact: {notices.Count(n => n.VolumeImpactMmbtu.HasValue)}");
            System.Console.WriteLine($"   Notices with effective dates: {notices.Count(n => n.AdditionalProperties.ContainsKey("EffectiveDate") || n.AdditionalProperties.ContainsKey("OutageStartDate"))}");
            
            System.Console.WriteLine($"\nMost Important Trading Signals:");
            System.Console.WriteLine("==================================");
            
            var topSignals = notices
                .Where(n => n.AdditionalProperties.GetValueOrDefault("TradingPriority") is "HIGH" or "CRITICAL")
                .OrderBy(n => n.AdditionalProperties.GetValueOrDefault("TradingPriority") == "CRITICAL" ? 0 : 1)
                .ThenBy(n => n.AdditionalProperties.GetValueOrDefault("CapacityPercentage"))
                .Take(8);
            
            foreach (var signal in topSignals)
            {
                var priority = signal.AdditionalProperties.GetValueOrDefault("TradingPriority");
                var icon = priority == "CRITICAL" ? "🔴" : "🟠";
                
                System.Console.WriteLine($"   {icon} {signal.Title}");
                if (signal.AdditionalProperties.ContainsKey("CapacityPercentage"))
                {
                    System.Console.WriteLine($"       Capacity: {signal.AdditionalProperties["CapacityPercentage"]}%");
                }
                if (signal.AdditionalProperties.ContainsKey("OutageStartDate"))
                {
                    System.Console.WriteLine($"       Dates: {signal.AdditionalProperties["OutageStartDate"]} to {signal.AdditionalProperties.GetValueOrDefault("OutageEndDate", "TBD")}");
                }
            }
            
            System.Console.WriteLine($"\nEnhanced Provider Success Metrics:");
            System.Console.WriteLine("=====================================");
            System.Console.WriteLine($"   Infragistics parsing: {(regularNotices.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   PDF download & validation: {(pdfNotices.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   PDF content extraction: {(capacityRestrictions.Any() ? "Working" : "Failed")}");
            System.Console.WriteLine($"   Capacity percentage parsing: {capacityRestrictions.Count(n => n.AdditionalProperties.ContainsKey("CapacityPercentage"))} / {capacityRestrictions.Count}");
            System.Console.WriteLine($"   Work order extraction: {capacityRestrictions.Count(n => !string.IsNullOrEmpty(n.AdditionalProperties.GetValueOrDefault("WorkOrderNumber")))} / {capacityRestrictions.Count}");
            System.Console.WriteLine($"   Date range extraction: {capacityRestrictions.Count(n => !string.IsNullOrEmpty(n.AdditionalProperties.GetValueOrDefault("OutageStartDate")))} / {capacityRestrictions.Count}");
            System.Console.WriteLine($"   Trading priority classification: {notices.Count(n => n.AdditionalProperties.ContainsKey("TradingPriority"))} / {notices.Count}");
            
            var successRate = notices.Count > 0 ? (int)(notices.Count(n => 
                !string.IsNullOrEmpty(n.Id) && 
                !string.IsNullOrEmpty(n.Title) && 
                n.NoticeDate > DateTime.MinValue &&
                !string.IsNullOrEmpty(n.NoticeType)
            ) * 100.0 / notices.Count) : 0;
            
            System.Console.WriteLine($"   Overall success rate: {successRate}%");
            
            if (successRate > 90 && capacityRestrictions.Any())
            {
                System.Console.WriteLine($"\n🎉 ENHANCED NGPL Provider is working excellently!");
                System.Console.WriteLine($"   - Infragistics grid parsing for regular notices");
                System.Console.WriteLine($"   - PDF download and validation");
                System.Console.WriteLine($"   - PdfPig text extraction from outage reports");
                System.Console.WriteLine($"   - Capacity restriction parsing with percentages");
                System.Console.WriteLine($"   - Work order and date extraction");
                System.Console.WriteLine($"   - Trading priority classification");
                System.Console.WriteLine($"   - Extracted {capacityRestrictions.Count} capacity restrictions");
                System.Console.WriteLine($"   - {capacityRestrictions.Count(n => n.AdditionalProperties.GetValueOrDefault("TradingPriority") is "HIGH" or "CRITICAL")} high-priority trading signals");
                System.Console.WriteLine($"   - Ready for the trading alert system!");
            }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        
        System.Console.WriteLine("\nNGPL provider test completed. Press any key to exit...");
        System.Console.ReadKey();
    }
}