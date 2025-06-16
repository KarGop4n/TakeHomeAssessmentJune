using Microsoft.Extensions.Logging;
using System.Globalization;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public class ColumbiaGulfCsvProvider : IPipelineDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ColumbiaGulfCsvProvider> _logger;
    private readonly List<(string Category, string Parameters)> _categories = new() 
    { 
        ("AllNotices", "") // Get complete dataset
    };

    public ColumbiaGulfCsvProvider(HttpClient httpClient, ILogger<ColumbiaGulfCsvProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string PipelineName => "Columbia Gulf Pipeline";

    public async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        foreach (var (category, parameters) in _categories)
        {
            try
            {
                _logger.LogInformation("Fetching {Category} notices from {Pipeline} via CSV", category, PipelineName);
                
                var categoryNotices = await GetNoticesForCategoryAsync(category, parameters, cancellationToken);
                allNotices.AddRange(categoryNotices);
                
                _logger.LogInformation("Retrieved {Count} {Category} notices from {Pipeline}", 
                    categoryNotices.Count, category, PipelineName);
                
                if (category != _categories.Last().Category)
                {
                    await Task.Delay(3000, cancellationToken);
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

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testUrl = "https://ebb.tceconnects.com/infopost/ReportViewer.aspx?/InfoPost/Notices&assetNbr=51&CritFlag=1&rs:Format=CSV";
            var response = await _httpClient.GetAsync(testUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(string category, string parameters, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        
        var csvUrl = string.IsNullOrEmpty(parameters)
            ? "https://ebb.tceconnects.com/infopost/ReportViewer.aspx?/InfoPost/Notices&assetNbr=51&rs:Format=CSV"
            : $"https://ebb.tceconnects.com/infopost/ReportViewer.aspx?/InfoPost/Notices&assetNbr=51&{parameters}&rs:Format=CSV";
        
        _logger.LogInformation("Fetching CSV data from: {Url}", csvUrl);
        
        var response = await _httpClient.GetAsync(csvUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var csvContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (string.IsNullOrEmpty(csvContent))
        {
            _logger.LogWarning("Empty CSV response for {Category} notices", category);
            return notices;
        }

        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        if (lines.Length < 2)
        {
            _logger.LogWarning("CSV has no data rows for {Category} notices", category);
            return notices;
        }

        var headers = ParseCsvLine(lines[0]);
        var columnMap = CreateColumnMap(headers);
        
        _logger.LogInformation("CSV has {RowCount} rows with {ColumnCount} columns", lines.Length - 1, headers.Length);
        
        for (int i = 1; i < lines.Length; i++)
        {
            try
            {
                var notice = ParseCsvRowToNotice(lines[i], columnMap, csvUrl);
                if (notice != null)
                {
                    notices.Add(notice);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing CSV row {RowNumber}", i + 1);
            }
        }

        var categoryCounts = notices.GroupBy(n => n.AdditionalProperties.GetValueOrDefault("Category", "Unknown"))
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation("Retrieved notices breakdown: {Breakdown}", 
            string.Join(", ", categoryCounts.Select(kv => $"{kv.Key}={kv.Value}")));

        return notices;
    }

    private PipelineNotice? ParseCsvRowToNotice(string csvRow, Dictionary<string, int> columnMap, string sourceUrl)
    {
        var values = ParseCsvLine(csvRow);
        
        if (values.Length < 5) 
            return null;

        var notice = new PipelineNotice
        {
            PipelineName = PipelineName,
            SourceUrl = sourceUrl
        };

        var criticalInd = "";
        var noticeTypeFromCsv = "";
        
        TryGetValue(values, columnMap, "CriticalInd", out criticalInd);
        TryGetValue(values, columnMap, "NoticeType", out noticeTypeFromCsv);

        // Categorize notices based on data
        if (criticalInd == "Y")
        {
            notice.AdditionalProperties["Category"] = "Critical";
        }
        else if (criticalInd == "N")
        {
            notice.AdditionalProperties["Category"] = "NonCritical";
        }
        else
        {
            notice.AdditionalProperties["Category"] = "Unknown";
        }

        // Category for planned maintenance
        if (noticeTypeFromCsv.Contains("Maintenance", StringComparison.OrdinalIgnoreCase) ||
            noticeTypeFromCsv.Contains("Planned", StringComparison.OrdinalIgnoreCase))
        {
            notice.AdditionalProperties["IsPlannedMaintenance"] = "true";
        }

        if (TryGetValue(values, columnMap, "NoticeId", out var noticeId))
        {
            notice.Id = noticeId;
            notice.AdditionalProperties["NoticeId"] = noticeId;
        }
        else
        {
            notice.Id = Guid.NewGuid().ToString();
        }

        if (TryGetValue(values, columnMap, "Subject", out var subject))
        {
            notice.Title = subject;
            notice.Description = subject;
        }

        if (TryGetValue(values, columnMap, "NoticeType", out var noticeType))
        {
            notice.NoticeType = noticeType;
        }

        if (TryGetValue(values, columnMap, "PostedDt", out var postedDt) && 
            DateTime.TryParse(postedDt, out var postDate))
        {
            notice.NoticeDate = postDate;
        }

        if (TryGetValue(values, columnMap, "EffBeginDt", out var effBeginDt) && 
            DateTime.TryParse(effBeginDt, out var effectiveDate))
        {
            notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
        }

        if (TryGetValue(values, columnMap, "EndDt", out var endDt) && 
            DateTime.TryParse(endDt, out var endDate))
        {
            notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
        }

        if (!string.IsNullOrEmpty(criticalInd))
        {
            notice.AdditionalProperties["Critical"] = criticalInd;
        }

        if (TryGetValue(values, columnMap, "TSPName", out var tspName))
        {
            notice.AdditionalProperties["TSPName"] = tspName;
        }

        if (TryGetValue(values, columnMap, "NoticeText", out var noticeText))
        {
            if (!string.IsNullOrEmpty(noticeText))
            {
                notice.Description = noticeText;
            }
        }

        ExtractLocationFromSubject(notice);
        
        ExtractVolumeInformation(notice);

        return notice;
    }

    private void ExtractLocationFromSubject(PipelineNotice notice)
    {
        var subject = notice.Title.ToLowerInvariant();
        
        var locationKeywords = new Dictionary<string, string>
        {
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "mississippi", "Mississippi" },
            { "alabama", "Alabama" },
            { "florida", "Florida" },
            { "henry hub", "Henry Hub" },
            { "gulf coast", "Gulf Coast" },
            { "mainline", "Mainline" },
            { "lateral", "Lateral" },
            { "tim", "TIM" }, // Transportation Improvement Maintenance
            { "capacity", "Capacity" },
            { "compressor station", "Compressor Station" },
            { "meter station", "Meter Station" }
        };

        foreach (var (keyword, location) in locationKeywords)
        {
            if (subject.Contains(keyword))
            {
                notice.Location = location;
                break;
            }
        }

        var facilityPatterns = new[]
        {
            @"(Line\s+[A-Z0-9-]+)", // "Line VM-107", "Line 10110", "Line PM3"
            @"(Compressor\s+Station\s*\w*)", // "Compressor Station", "Cobb Compressor Station"
            @"(\w+\s+Compressor\s+Station)", // "Cobb Compressor Station"
            @"(Station\s+\d+)", // "Station 123"
            @"(Meter\s+\d+)", // "Meter 456"
            @"([A-Z]{2,4}\s+Loop)", // "WB Loop"
            @"(Extension\s+Maintenance)", // "PM3 Extension Maintenance"
        };

        foreach (var pattern in facilityPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(notice.Title, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var facility = match.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(notice.Location))
                {
                    notice.Location = facility;
                }
                else
                {
                    notice.AdditionalProperties["Facility"] = facility;
                }
                break;
            }
        }

        var locationInParens = System.Text.RegularExpressions.Regex.Match(notice.Title, @"\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (locationInParens.Success && string.IsNullOrEmpty(notice.Location))
        {
            var locationText = locationInParens.Groups[1].Value.Trim();
            if (locationText.Length > 2 && locationText.Length < 20)
            {
                notice.Location = locationText;
            }
        }
    }

    private void ExtractVolumeInformation(PipelineNotice notice)
    {
        var searchText = $"{notice.Title} {notice.Description}";
        
        var volumePatterns = new[]
        {
            @"([\d,\.]+)\s*(MMBtu|mmbtu|MMBTU)(?:/d|/day|\s*per\s*day)?",
            @"([\d,\.]+)\s*(Dth|dth|DTH)(?:/d|/day|\s*per\s*day)?",
            @"([\d,\.]+)\s*(MCF|mcf|Mcf)(?:/d|/day|\s*per\s*day)?",
            @"([\d,\.]+)\s*(BCF|bcf|Bcf)(?:/d|/day|\s*per\s*day)?",
            @"TIM\s+for.*?([\d,\.]+)", // Transportation Improvement Maintenance
            @"capacity.*?([\d,\.]+)", // Capacity postings
            @"reduction.*?([\d,\.]+)", // Reduction notices
            @"Line\s+\w+-?\d+.*?([\d,\.]+)", // Line-specific volumes like "Line VM-107"
            @"pipeline\s+capacity.*?([\d,\.]+)", // Pipeline capacity mentions
            @"maintenance.*?impact.*?([\d,\.]+)", // Maintenance impact volumes
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

    private Dictionary<string, int> CreateColumnMap(string[] headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int i = 0; i < headers.Length; i++)
        {
            var header = headers[i].Trim();
            if (!string.IsNullOrEmpty(header))
            {
                map[header] = i;
            }
        }

        _logger.LogDebug("Column mapping: {Columns}", string.Join(", ", map.Keys));
        return map;
    }

    private bool TryGetValue(string[] values, Dictionary<string, int> columnMap, string columnName, out string value)
    {
        value = string.Empty;
        
        if (columnMap.TryGetValue(columnName, out var index) && 
            index < values.Length)
        {
            value = values[index].Trim();
            return !string.IsNullOrEmpty(value);
        }
        
        return false;
    }

    private string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        
        result.Add(current.ToString());
        return result.ToArray();
    }
}