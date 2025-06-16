using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public class AnrPipelineDataProvider : BasePipelineDataProvider
{
    // Fetches notices from Critical, NonCritical, and Planned Service Outage categories
    private readonly List<string> _categories = new() { "Critical", "NonCritical", "PlanSvcOut" };

    public AnrPipelineDataProvider(HttpClient httpClient, ILogger<AnrPipelineDataProvider> logger) 
        : base(httpClient, logger, CreateConfiguration())
    {
    }

    public override string PipelineName => "ANR Pipeline";

    public override async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        foreach (var category in _categories)
        {
            try
            {
                _logger.LogInformation("Fetching {Category} notices from {Pipeline}", category, PipelineName);
                
                var categoryNotices = await GetNoticesForCategoryAsync(category, cancellationToken);
                allNotices.AddRange(categoryNotices);
                
                _logger.LogInformation("Retrieved {Count} {Category} notices from {Pipeline}", 
                    categoryNotices.Count, category, PipelineName);
                
                if (category != _categories.Last())
                {
                    await Task.Delay(_config.RequestDelayMs, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching {Category} notices from {Pipeline}", category, PipelineName);
            }
        }

        _logger.LogInformation("Successfully fetched {Count} total notices from {Pipeline}", allNotices.Count, PipelineName);
        return allNotices;
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(string category, CancellationToken cancellationToken)
    {
        var url = $"{_config.BaseUrl}/Notices/Notices.asp?sPipelineCode=ANR&sSubCategory={category}";
        
        await Task.Delay(_config.RequestDelayMs, cancellationToken);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var notices = await ParseNoticesFromHtmlAsync(htmlContent, cancellationToken);
        
        foreach (var notice in notices)
        {
            var categoryName = category switch
            {
                "PlanSvcOut" => "Planned Service Outage",
                "Critical" => "Critical",
                "NonCritical" => "Non-Critical",
                _ => category
            };
            
            notice.AdditionalProperties["Category"] = categoryName;
            notice.SourceUrl = url; 
            
            if (category == "PlanSvcOut")
            {
                notice.AdditionalProperties["TradingPriority"] = "HIGH";
                notice.AdditionalProperties["OutageType"] = "Planned Service Outage";
            }
        }
        
        return notices;
    }

    private static PipelineConfiguration CreateConfiguration()
    {
        return new PipelineConfiguration
        {
            Name = "ANR",
            BaseUrl = "https://ebb.anrpl.com",
            NoticesPath = "/Notices/Notices.asp?sPipelineCode=ANR&sSubCategory=Critical",
            IsEnabled = true,
            RequestDelayMs = 2000,
            ParsingRules = new ParsingRules
            {
                NoticeRowSelector = "//table[@border='1']//tr[position()>1]",
                TitleSelector = ".//td[6]",        // Subject column (6th column)
                TypeSelector = ".//td[1]",         // Notice Type column (1st column)  
                DateSelector = ".//td[2]",         // Posted Date/Time column (2nd column)
                LocationSelector = ".//td[6]",     // Extract location from Subject column
                LinkSelector = ".//td[6]/a",       // Look for links in Subject column
                DateFormat = "MM/dd/yyyy HH:mm",   // Time
                VolumeRegexPatterns = new List<string>
                {
                    @"([\d,\.]+)\s*(MMBtu|mmbtu|MMBTU)",
                    @"([\d,\.]+)\s*(Dth|dth|DTH)", 
                    @"([\d,\.]+)\s*(MCF|mcf|Mcf)",
                    @"([\d,\.]+)\s*(BCF|bcf|Bcf)",
                    @"CAPACITY\s+REDUCTION.*?([\d,\.]+)",  
                    @"reduction\s+of\s+([\d,\.]+)",        // Alternative pattern
                    @"outage.*?([\d,\.]+)",                // Planned outage volumes
                    @"maintenance.*?([\d,\.]+)",           // Maintenance-related volumes
                    @"limited.*?to.*?([\d,\.]+)",          // Limited capacity during outages
                }
            }
        };
    }

    protected override async Task<PipelineNotice?> ParseSingleNoticeAsync(HtmlNode node, CancellationToken cancellationToken)
    {
        var notice = await base.ParseSingleNoticeAsync(node, cancellationToken);
        
        if (notice == null) return null;

        notice.NoticeType = CleanNoticeType(notice.NoticeType);

        ExtractLocationFromSubject(notice);
        ExtractNoticeIdFromRow(node, notice);
        
        if (notice.AdditionalProperties.GetValueOrDefault("Category") == "Planned Service Outage")
        {
            ExtractPlannedOutageDetails(notice);
        }
        
        return notice;
    }

    private void ExtractPlannedOutageDetails(PipelineNotice notice)
    {
        var subject = notice.Title.ToLowerInvariant();
        
        // Extract planned outage specific information
        var outageKeywords = new[]
        {
            "planned maintenance", "scheduled maintenance", "planned outage", 
            "scheduled outage", "facility maintenance", "pipeline maintenance",
            "compressor maintenance", "station maintenance"
        };

        var foundOutageTypes = new List<string>();
        foreach (var keyword in outageKeywords)
        {
            if (subject.Contains(keyword))
            {
                foundOutageTypes.Add(keyword);
            }
        }

        if (foundOutageTypes.Any())
        {
            notice.AdditionalProperties["MaintenanceTypes"] = string.Join(", ", foundOutageTypes);
        }

        var durationPatterns = new[]
        {
            @"(\d+)\s*(?:hour|hr)s?",           // "8 hours", "24 hrs"
            @"(\d+)\s*(?:day|days)",            // "3 days"
            @"(\d+)\s*(?:week|weeks)",          // "2 weeks"
            @"approximately\s+(\d+)\s*(?:hour|day)s?", // "approximately 12 hours"
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

        var capacityPatterns = new[]
        {
            @"capacity\s+(?:will\s+be\s+)?(?:limited|reduced)\s+to.*?([\d,\.]+)",
            @"(?:limited|reduced)\s+capacity.*?([\d,\.]+)",
            @"flow\s+(?:will\s+be\s+)?(?:limited|restricted)\s+to.*?([\d,\.]+)",
        };

        foreach (var pattern in capacityPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(notice.Title, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var capacity))
            {
                if (notice.VolumeImpactMmbtu == null || capacity < notice.VolumeImpactMmbtu) // For outages, lower capacity is the constraint
                {
                    notice.VolumeImpactMmbtu = capacity;
                    notice.VolumeUnit = "MMBTU/day"; // Assume daily capacity
                    notice.AdditionalProperties["CapacityConstraint"] = $"Limited to {capacity:N0} MMBTU/day during maintenance";
                }
                break;
            }
        }

        _logger.LogDebug("Enhanced planned outage notice {NoticeId} with maintenance details", notice.Id);
    }

    private string CleanNoticeType(string noticeType)
    {
        if (string.IsNullOrEmpty(noticeType)) return noticeType;
        
        return noticeType
            .Replace("&nbsp;", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Trim();
    }

    private void ExtractLocationFromSubject(PipelineNotice notice)
    {
        var subject = notice.Title.ToLowerInvariant();
        
        var locationKeywords = new Dictionary<string, string>
        {
            { "southeast mainline", "Southeast Mainline" },
            { "southwest", "Southwest" },
            { "michigan leg", "Michigan Leg" },
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "oklahoma", "Oklahoma" },
            { "arkansas", "Arkansas" },
            { "henry hub", "Henry Hub" },
            { "compressor station", "Compressor Station" },
            { "meter station", "Meter Station" },
            { "mainline", "Mainline" }
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
            var match = System.Text.RegularExpressions.Regex.Match(notice.Title, @"-\s([^-()]+(?:\([^)]+\))?)\s*(?:\(|$)");
            if (match.Success)
            {
                notice.Location = match.Groups[1].Value.Trim();
            }
        }
    }

    private void ExtractNoticeIdFromRow(HtmlNode node, PipelineNotice notice)
    {
        // Extract Notice ID from the 5th column
        var noticeIdNode = node.SelectSingleNode(".//td[5]");
        if (noticeIdNode != null)
        {
            var noticeId = noticeIdNode.InnerText?.Trim();
            if (!string.IsNullOrEmpty(noticeId))
            {
                notice.Id = noticeId;
                notice.AdditionalProperties["NoticeId"] = noticeId;
            }
        }

        // Extract effective and end dates
        var effectiveDateNode = node.SelectSingleNode(".//td[3]");
        var endDateNode = node.SelectSingleNode(".//td[4]");
        
        if (effectiveDateNode != null && DateTime.TryParse(effectiveDateNode.InnerText?.Trim(), out var effectiveDate))
        {
            notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
        }
        
        if (endDateNode != null && DateTime.TryParse(endDateNode.InnerText?.Trim(), out var endDate))
        {
            notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
        }
    }
}