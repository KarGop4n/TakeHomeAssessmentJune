using Microsoft.Extensions.Logging;
using System.Text.Json;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public class CreoleTrailPipelineDataProvider : IPipelineDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CreoleTrailPipelineDataProvider> _logger;
    
    private readonly Dictionary<string, int> _pipelineConfigs = new()
    {
        { "CTPL", 200 }, // Creole Trail Pipeline
        { "CCPL", 400 }  // Corpus Christi Pipeline
    };

    private readonly Dictionary<string, int> _noticeCategories = new()
    {
        { "Critical", 9 },
        { "Non-Critical", 10 },
        { "Planned Service Outages", 11 } 
    };

    public CreoleTrailPipelineDataProvider(HttpClient httpClient, ILogger<CreoleTrailPipelineDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string PipelineName => "Creole Trail & Corpus Christi (Cheniere)";

    public async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        foreach (var (pipelineName, tspNo) in _pipelineConfigs)
        {
            try
            {
                _logger.LogInformation("Fetching notices from {Pipeline} (tspNo={TspNo})", pipelineName, tspNo);
                
                var criticalNotices = await GetNoticesForCategoryAsync(pipelineName, tspNo, _noticeCategories["Critical"], "Critical", cancellationToken);
                var nonCriticalNotices = await GetNoticesForCategoryAsync(pipelineName, tspNo, _noticeCategories["Non-Critical"], "Non-Critical", cancellationToken);
                var plannedOutageNotices = await GetNoticesForCategoryAsync(pipelineName, tspNo, _noticeCategories["Planned Service Outages"], "Planned Service Outages", cancellationToken);
                
                allNotices.AddRange(criticalNotices);
                allNotices.AddRange(nonCriticalNotices);
                allNotices.AddRange(plannedOutageNotices);
                
                _logger.LogInformation("Retrieved {CriticalCount} critical, {NonCriticalCount} non-critical, and {PlannedCount} planned outage notices from {Pipeline}", 
                    criticalNotices.Count, nonCriticalNotices.Count, plannedOutageNotices.Count, pipelineName);
                
                await Task.Delay(2000, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching notices from {Pipeline} (tspNo={TspNo})", pipelineName, tspNo);
            }
        }

        _logger.LogInformation("Successfully fetched {Count} total notices from Cheniere pipelines", allNotices.Count);
        return allNotices;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testUrl = "https://lngconnectionapi.cheniere.com/api/Notice/GetNoticesByPageId?tspNo=200&pageId=9";
            var response = await _httpClient.GetAsync(testUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(
        string pipelineName, 
        int tspNo, 
        int pageId, 
        string category, 
        CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        
        var url = $"https://lngconnectionapi.cheniere.com/api/Notice/GetNoticesByPageId?tspNo={tspNo}&pageId={pageId}";
        
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (string.IsNullOrEmpty(jsonContent) || jsonContent == "[]")
            {
                _logger.LogDebug("No {Category} notices found for {Pipeline}", category, pipelineName);
                return notices;
            }

            var jsonArray = JsonDocument.Parse(jsonContent).RootElement;
            
            if (jsonArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var jsonNotice in jsonArray.EnumerateArray())
                {
                    var notice = ParseJsonToNotice(jsonNotice, pipelineName, category, url);
                    if (notice != null)
                    {
                        if (category == "Planned Service Outages")
                        {
                            EnhancePlannedOutageNotice(notice);
                        }
                        
                        notices.Add(notice);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Category} notices from {Pipeline} (tspNo={TspNo})", 
                category, pipelineName, tspNo);
        }

        return notices;
    }

    private PipelineNotice? ParseJsonToNotice(JsonElement jsonNotice, string pipelineName, string category, string sourceUrl)
    {
        try
        {
            if (!jsonNotice.TryGetProperty("noticeId", out var noticeIdElement))
                return null;

            var notice = new PipelineNotice
            {
                Id = noticeIdElement.GetInt32().ToString(),
                PipelineName = pipelineName,
                SourceUrl = sourceUrl
            };

            if (jsonNotice.TryGetProperty("subject", out var subjectElement))
            {
                notice.Title = subjectElement.GetString()?.Trim() ?? string.Empty;
                notice.Description = notice.Title; // Use subject as description too
            }

            if (jsonNotice.TryGetProperty("noticeType", out var typeElement))
            {
                notice.NoticeType = typeElement.GetString()?.Trim() ?? string.Empty;
            }

            if (jsonNotice.TryGetProperty("postingDateTime", out var postingElement))
            {
                if (DateTime.TryParse(postingElement.GetString(), out var postingDate))
                {
                    notice.NoticeDate = postingDate;
                }
            }

            ExtractLocationFromSubject(notice);

            AddAdditionalProperties(notice, jsonNotice, category);

            ExtractVolumeInformation(notice);

            return notice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing notice JSON for {Pipeline}", pipelineName);
            return null;
        }
    }

    private void EnhancePlannedOutageNotice(PipelineNotice notice)
    {
        notice.AdditionalProperties["TradingPriority"] = "HIGH";
        notice.AdditionalProperties["OutageType"] = "Planned Service Outage";
        
        var subject = notice.Title.ToLowerInvariant();
        
        var lngOutageTypes = new[]
        {
            "terminal maintenance", "facility maintenance", "compressor maintenance",
            "pipeline maintenance", "planned outage", "scheduled outage", 
            "liquefaction maintenance", "regasification maintenance", "loading maintenance"
        };

        var foundOutageTypes = new List<string>();
        foreach (var outageType in lngOutageTypes)
        {
            if (subject.Contains(outageType))
            {
                foundOutageTypes.Add(outageType);
            }
        }

        if (foundOutageTypes.Any())
        {
            notice.AdditionalProperties["LNGMaintenanceTypes"] = string.Join(", ", foundOutageTypes);
        }

        var durationPatterns = new[]
        {
            @"(\d+)\s*(?:day|days)",                    // "5 days", "30 days"
            @"(\d+)\s*(?:week|weeks)",                  // "2 weeks", "3 weeks"
            @"(\d+)\s*(?:month|months)",                // "1 month"
            @"approximately\s+(\d+)\s*(?:day|week|month)s?", // "approximately 14 days"
            @"scheduled\s+for\s+(\d+)\s*(?:day|week)s?", // "scheduled for 10 days"
        };

        foreach (var pattern in durationPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(notice.Title, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                notice.AdditionalProperties["EstimatedDuration"] = match.Value;
                break;
            }
        }

        var lngCapacityPatterns = new[]
        {
            @"train\s+(\d+).*?(?:offline|unavailable|shutdown)", // "Train 1 offline"
            @"liquefaction\s+capacity.*?reduced.*?(\d+)%", // "liquefaction capacity reduced 50%"
            @"terminal\s+capacity.*?limited.*?([\d,\.]+)", // "terminal capacity limited to X"
            @"export\s+capacity.*?([\d,\.]+)\s*(MTPA|mtpa|Mtpa)", // "export capacity 5.5 MTPA"
            @"(?:berth|dock|loading).*?unavailable", // Loading berth unavailable
        };

        foreach (var pattern in lngCapacityPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(notice.Title, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (match.Groups.Count > 1 && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var capacity))
                {
                    notice.AdditionalProperties["LNGCapacityImpact"] = $"{capacity} {(match.Groups.Count > 2 ? match.Groups[2].Value : "units")}";
                }
                else
                {
                    notice.AdditionalProperties["LNGFacilityImpact"] = match.Value;
                }
                break;
            }
        }

        _logger.LogDebug("Enhanced planned LNG outage notice {NoticeId} with facility-specific details", notice.Id);
    }

    private void AddAdditionalProperties(PipelineNotice notice, JsonElement jsonNotice, string category)
    {
        notice.AdditionalProperties["Category"] = category;

        var jsonProperties = new Dictionary<string, string>
        {
            { "noticeStatus", "NoticeStatus" },
            { "noticeResponseIndicator", "ResponseIndicator" },
            { "effectiveDateTime", "EffectiveDate" },
            { "endDateTime", "EndDate" },
            { "responseDateTime", "ResponseDate" },
            { "pageId", "PageId" },
            { "isExpired", "IsExpired" },
            { "posted", "Posted" },
            { "emailed", "Emailed" }
        };

        foreach (var (jsonKey, propertyKey) in jsonProperties)
        {
            if (jsonNotice.TryGetProperty(jsonKey, out var element))
            {
                var value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => element.GetRawText()
                };

                if (!string.IsNullOrEmpty(value))
                {
                    notice.AdditionalProperties[propertyKey] = value;
                }
            }
        }
    }

    private void ExtractLocationFromSubject(PipelineNotice notice)
    {
        var subject = notice.Title.ToLowerInvariant();
        
        var locationKeywords = new Dictionary<string, string>
        {
            { "sabine pass", "Sabine Pass LNG Terminal" },
            { "corpus christi", "Corpus Christi LNG Terminal" },
            { "creole trail", "Creole Trail Pipeline" },
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "gulf coast", "Gulf Coast" },
            { "cameron", "Cameron LNG" },
            { "calcasieu", "Calcasieu Pass" },
            { "sinton", "Sinton Compressor Station" },
            { "compressor station", "Compressor Station" },
            { "meter station", "Meter Station" },
            { "terminal", "LNG Terminal" },
            { "train", "LNG Train" },
            { "berth", "Loading Berth" }, // Ship loading berths
            { "dock", "Loading Dock" }
        };

        foreach (var (keyword, location) in locationKeywords)
        {
            if (subject.Contains(keyword))
            {
                notice.Location = location;
                break;
            }
        }

        if (string.IsNullOrEmpty(notice.Location))
        {
            var locationMatch = System.Text.RegularExpressions.Regex.Match(notice.Title, 
                @"(?:CTPL|CCPL)\s*[–-]\s*([^–-]+)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (locationMatch.Success)
            {
                var extractedLocation = locationMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(extractedLocation) && !extractedLocation.Contains("Planned") && !extractedLocation.Contains("Summary"))
                {
                    notice.Location = extractedLocation;
                }
            }
        }
    }

    private void ExtractVolumeInformation(PipelineNotice notice)
    {
        var searchText = $"{notice.Title} {notice.Description}";
        
        var volumePatterns = new[]
        {
            @"([\d,\.]+)\s*(BCF|bcf|Bcf)(?:/d|/day|\s*per\s*day)?", // BCF/day is common for LNG
            @"([\d,\.]+)\s*(MMCF|mmcf|MMcf)(?:/d|/day|\s*per\s*day)?", // MMCF/day
            @"([\d,\.]+)\s*(MTPA|mtpa|Mtpa)", // Million Tonnes Per Annum for LNG
            @"([\d,\.]+)\s*(TBtu|tbtu|TBTU)", // Trillion BTU
            @"([\d,\.]+)\s*(MMBtu|mmbtu|MMBTU)", // Million BTU
            @"([\d,\.]+)\s*(Dth|dth|DTH)", // Dekatherms
            @"capacity.*?(?:reduced|limited).*?([\d,\.]+)", // Capacity reductions during outages
            @"train.*?capacity.*?([\d,\.]+)", // LNG train capacity
            @"export.*?capacity.*?([\d,\.]+)", // Export capacity impacts
            @"(?:unavailable|offline).*?([\d,\.]+)", // Offline capacity
            @"maintenance.*?impact.*?([\d,\.]+)", // Maintenance capacity impacts
        };

        foreach (var pattern in volumePatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(searchText, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var volume))
            {
                notice.VolumeImpactMmbtu = volume;
                notice.VolumeUnit = match.Groups.Count > 2 ? match.Groups[2].Value : "MMBTU";
                break;
            }
        }
    }
}