using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public class SabinePipelineDataProvider : BasePipelineDataProvider
{
    private readonly List<(string Category, string TypeParam)> _categories = new() 
    { 
        ("Critical", "1"), 
        ("Planned Service Outage", "3") 
    };

    public SabinePipelineDataProvider(HttpClient httpClient, ILogger<SabinePipelineDataProvider> logger) 
        : base(httpClient, logger, CreateConfiguration())
    {
    }

    public override string PipelineName => "Sabine Pipeline";

    public override async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        foreach (var (category, typeParam) in _categories)
        {
            try
            {
                _logger.LogInformation("Fetching {Category} notices from {Pipeline}", category, PipelineName);
                
                var categoryNotices = await GetNoticesForCategoryAsync(category, typeParam, cancellationToken);
                allNotices.AddRange(categoryNotices);
                
                _logger.LogInformation("Retrieved {Count} {Category} notices from {Pipeline}", 
                    categoryNotices.Count, category, PipelineName);
                
                if (category != _categories.Last().Category)
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

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(string category, string typeParam, CancellationToken cancellationToken)
    {
        var url = $"{_config.BaseUrl}/notices.cfm?type={typeParam}";
        
        await Task.Delay(_config.RequestDelayMs, cancellationToken);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var notices = await ParseNoticesFromHtmlAsync(htmlContent, cancellationToken);
        
        foreach (var notice in notices)
        {
            notice.AdditionalProperties["Category"] = category;
            notice.SourceUrl = url; 
        }
        
        return notices;
    }

    private static PipelineConfiguration CreateConfiguration()
    {
        return new PipelineConfiguration
        {
            Name = "Sabine",
            BaseUrl = "https://www.gasnom.com/ip/SABINE",
            NoticesPath = "/notices.cfm?type=1", 
            IsEnabled = true,
            RequestDelayMs = 2000,
            ParsingRules = new ParsingRules
            {
                NoticeRowSelector = "//table//tr[td and position()>1]",
                TitleSelector = ".//td[6]",        // Subject column (6th column)
                TypeSelector = ".//td[1]",         // Notice Type column (1st column)  
                DateSelector = ".//td[2]",         // Posted Date/Time column (2nd column)
                LocationSelector = ".//td[6]",     // Extract location from Subject column
                LinkSelector = ".//td[6]/a",       // Look for links in Subject column
                DateFormat = "MMM dd, yyyy HH:mm tt", // Sabine uses format like "Jun 13, 2025 08:24:08 AM"
                VolumeRegexPatterns = new List<string>
                {
                    @"([\d,\.]+)\s*(MMBtu|mmbtu|MMBTU)",
                    @"([\d,\.]+)\s*(Dth|dth|DTH)", 
                    @"([\d,\.]+)\s*(MCF|mcf|Mcf)",
                    @"([\d,\.]+)\s*(BCF|bcf|Bcf)",
                    @"CAPACITY\s+RESTRICTION.*?([\d,\.]+)",  
                    @"maintenance.*?([\d,\.]+)",              
                    @"hub.*?([\d,\.]+)",                      
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
        ExtractNoticeIdAndDatesFromRow(node, notice);
        
        return notice;
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
            { "sabine hub", "Sabine Hub" },
            { "henry hub", "Henry Hub" },
            { "sabine pass", "Sabine Pass" },
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "orange", "Orange" },
            { "beaumont", "Beaumont" },
            { "port arthur", "Port Arthur" },
            { "compressor station", "Compressor Station" },
            { "meter station", "Meter Station" },
            { "mainline", "Mainline" },
            { "lateral", "Lateral" }
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
            // Look for patterns: "Station 123" or "Meter 456"
            var locationMatch = System.Text.RegularExpressions.Regex.Match(notice.Title, 
                @"(Station\s+\d+|Meter\s+\d+|Compressor\s+\w+)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (locationMatch.Success)
            {
                notice.Location = locationMatch.Groups[1].Value.Trim();
            }
        }
    }

    private void ExtractNoticeIdAndDatesFromRow(HtmlNode node, PipelineNotice notice)
    {
        var cells = node.SelectNodes(".//td");
        if (cells == null || cells.Count < 7) return;

        var noticeIdNode = cells[4];
        if (noticeIdNode != null)
        {
            var noticeId = noticeIdNode.InnerText?.Trim();
            if (!string.IsNullOrEmpty(noticeId))
            {
                notice.Id = noticeId;
                notice.AdditionalProperties["NoticeId"] = noticeId;
            }
        }

        var effectiveDateNode = cells[2];
        if (effectiveDateNode != null)
        {
            var effectiveDateText = effectiveDateNode.InnerText?.Trim();
            if (!string.IsNullOrEmpty(effectiveDateText) && DateTime.TryParse(effectiveDateText, out var effectiveDate))
            {
                notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
            }
        }
        
        var endDateNode = cells[3];
        if (endDateNode != null)
        {
            var endDateText = endDateNode.InnerText?.Trim();
            if (!string.IsNullOrEmpty(endDateText) && DateTime.TryParse(endDateText, out var endDate))
            {
                notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
            }
        }

        if (cells.Count > 6)
        {
            var responseDateNode = cells[6];
            if (responseDateNode != null)
            {
                var responseDateText = responseDateNode.InnerText?.Trim();
                if (!string.IsNullOrEmpty(responseDateText) && DateTime.TryParse(responseDateText, out var responseDate))
                {
                    notice.AdditionalProperties["ResponseDate"] = responseDate.ToString("yyyy-MM-dd HH:mm");
                }
            }
        }
    }

    protected override void ExtractVolumeInformation(PipelineNotice notice)
    {
        base.ExtractVolumeInformation(notice);

        var searchText = $"{notice.Title} {notice.Description}";
        
        var sabinePatterns = new[]
        {
            @"hub.*?capacity.*?([\d,\.]+)\s*(Dth|MMBTU|MCF)", // Hub capacity mentions
            @"restriction.*?([\d,\.]+)\s*(Dth|MMBTU|MCF)",    // Capacity restrictions
            @"available.*?([\d,\.]+)\s*(Dth|MMBTU|MCF)",      // Available capacity
            @"limited.*?to.*?([\d,\.]+)\s*(Dth|MMBTU|MCF)",   // Limited to X capacity
        };

        foreach (var pattern in sabinePatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(searchText, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var volume))
            {
                if (notice.VolumeImpactMmbtu == null || volume > notice.VolumeImpactMmbtu)
                {
                    notice.VolumeImpactMmbtu = volume;
                    notice.VolumeUnit = match.Groups[2].Value;
                }
                break;
            }
        }
    }
}