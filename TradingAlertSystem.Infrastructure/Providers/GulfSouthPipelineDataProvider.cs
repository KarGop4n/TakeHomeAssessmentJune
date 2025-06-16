using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public class GulfSouthPipelineDataProvider : IPipelineDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GulfSouthPipelineDataProvider> _logger;
    private readonly string _apiEndpoint = "https://reporting.prod.bwpmlp.org/infopost/noticedetails";

    public GulfSouthPipelineDataProvider(HttpClient httpClient, ILogger<GulfSouthPipelineDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.gasquest.com");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.gasquest.com/");
        _httpClient.DefaultRequestHeaders.Add("DNT", "1");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        _httpClient.DefaultRequestHeaders.Add("Priority", "u=0");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:138.0) Gecko/20100101 Firefox/138.0");
    }

    public string PipelineName => "Gulf South Pipeline";

    public async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        try
        {
            _logger.LogInformation("Fetching notices from {Pipeline} via REST API", PipelineName);

            var tradingNotices = await GetNoticesForCategoriesAsync("Critical|Planned Service Outage", cancellationToken);
            allNotices.AddRange(tradingNotices);

            _logger.LogInformation("Successfully fetched {Count} notices from {Pipeline}", allNotices.Count, PipelineName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notices from {Pipeline}", PipelineName);
        }

        return allNotices;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testPayload = new
            {
                filterSupersededTerminatedNotices = true,
                noticeCategory = "Critical",
                pageNumber = 1,
                pageSize = 1,
                sortDescending = true,
                tspId = 1
            };

            var json = JsonSerializer.Serialize(testPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_apiEndpoint, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking {Pipeline} availability", PipelineName);
            return false;
        }
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoriesAsync(string categories, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();

        try
        {
            var payload = new
            {
                filterSupersededTerminatedNotices = true,
                noticeCategory = categories,
                pageNumber = 1,
                pageSize = 100,
                sortDescending = true,
                tspId = 1
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("Fetching notices for categories: {Categories}", categories);

            var response = await _httpClient.PostAsync(_apiEndpoint, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (string.IsNullOrEmpty(responseBody))
            {
                _logger.LogWarning("Empty response from Gulf South API");
                return notices;
            }

            // Parse the JSON response
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (!root.TryGetProperty("notices", out var noticesArray))
            {
                _logger.LogWarning("No 'notices' property found in Gulf South API response");
                return notices;
            }

            if (!root.TryGetProperty("totalCount", out var totalCountElement))
            {
                _logger.LogWarning("No 'totalCount' property found in Gulf South API response");
            }
            else
            {
                var totalCount = totalCountElement.GetString();
                _logger.LogInformation("Gulf South API returned {Count} total notices", totalCount);
            }

            foreach (var noticeElement in noticesArray.EnumerateArray())
            {
                try
                {
                    var notice = ParseNoticeFromJson(noticeElement);
                    if (notice != null)
                    {
                        notices.Add(notice);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing individual notice from Gulf South API");
                }
            }

            _logger.LogInformation("Successfully parsed {Count} notices from Gulf South API", notices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notices for categories {Categories}", categories);
        }

        return notices;
    }

    private PipelineNotice? ParseNoticeFromJson(JsonElement noticeElement)
    {
        try
        {
            if (!noticeElement.TryGetProperty("noticeID", out var noticeIdElement))
            {
                _logger.LogWarning("Notice missing noticeID property");
                return null;
            }

            var notice = new PipelineNotice
            {
                Id = noticeIdElement.GetInt32().ToString(),
                PipelineName = PipelineName,
                SourceUrl = _apiEndpoint 
            };

            if (noticeElement.TryGetProperty("subject", out var subjectElement))
            {
                notice.Title = subjectElement.GetString()?.Trim() ?? string.Empty;
                notice.Description = notice.Title; // Use title as description initially
            }

            if (noticeElement.TryGetProperty("noticeTypeDescription", out var typeElement))
            {
                notice.NoticeType = typeElement.GetString()?.Trim() ?? string.Empty;
            }

            if (noticeElement.TryGetProperty("dateTimePosting", out var postingElement))
            {
                if (DateTime.TryParse(postingElement.GetString(), out var postingDate))
                {
                    notice.NoticeDate = postingDate;
                }
            }

            if (noticeElement.TryGetProperty("dateTimeNoticeEffective", out var effectiveElement))
            {
                if (DateTime.TryParse(effectiveElement.GetString(), out var effectiveDate))
                {
                    notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
                }
            }

            if (noticeElement.TryGetProperty("dateTimeNoticeEnd", out var endElement))
            {
                if (DateTime.TryParse(endElement.GetString(), out var endDate))
                {
                    notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
                }
            }

            if (noticeElement.TryGetProperty("noticeStatusDescription", out var statusElement))
            {
                notice.AdditionalProperties["NoticeStatus"] = statusElement.GetString()?.Trim() ?? string.Empty;
            }

            if (noticeElement.TryGetProperty("fileNameWithExtension", out var fileElement))
            {
                var fileName = fileElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(fileName))
                {
                    notice.AdditionalProperties["AttachmentFile"] = fileName;
                }
            }

            ExtractLocationFromSubject(notice);

            ExtractVolumeInformation(notice);

            ExtractPipelineSegments(notice);

            return notice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing notice JSON element");
            return null;
        }
    }

    private void ExtractLocationFromSubject(PipelineNotice notice)
    {
        var subject = notice.Title.ToLowerInvariant();

        var locationKeywords = new Dictionary<string, string>
        {
            { "carthage junction", "Carthage Junction" },
            { "carthage", "Carthage" },
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "mississippi", "Mississippi" },
            { "alabama", "Alabama" },
            { "henry hub", "Henry Hub" },
            { "gulf coast", "Gulf Coast" },
            { "compressor station", "Compressor Station" },
            { "meter station", "Meter Station" },
            { "mainline", "Mainline" },
            { "lateral", "Lateral" },
            { "index", "Index Pipeline" },
            { "junction", "Junction" }
        };

        foreach (var (keyword, location) in locationKeywords)
        {
            if (subject.Contains(keyword))
            {
                notice.Location = location;
                break;
            }
        }

        var facilityMatch = System.Text.RegularExpressions.Regex.Match(notice.Title,
            @"([\w\s]+(?:Compressor|Meter|Junction|Station|Index))\s+(?:Station|Maintenance|Pipeline)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (facilityMatch.Success)
        {
            var facilityName = facilityMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(notice.Location) || notice.Location == "Compressor Station")
            {
                notice.Location = facilityName;
            }
            notice.AdditionalProperties["Facility"] = facilityName;
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
            @"capacity.*?([\d,\.]+)",
            @"flow.*?([\d,\.]+)",
            @"reduction.*?([\d,\.]+)",
            @"limit.*?([\d,\.]+)",
            @"available.*?([\d,\.]+)"
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

    private void ExtractPipelineSegments(PipelineNotice notice)
    {
        var segments = new List<string>();

        var segmentPatterns = new[]
        {
            @"(Index\s+\d+(?:-\d+)?)", // "Index 11-15", "Index 1"
            @"(Line\s+\d+)", // "Line 100"
            @"(Station\s+\d+)", // "Station 123"
            @"(Meter\s+\d+)", // "Meter 456"
            @"(Compressor\s+\w+)", // "Compressor Station"
            @"(Junction\s+\w+)" // "Junction Compressor"
        };

        foreach (var pattern in segmentPatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(notice.Title, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                segments.Add(match.Groups[1].Value);
            }
        }

        if (segments.Any())
        {
            notice.AdditionalProperties["PipelineSegments"] = string.Join(", ", segments.Distinct());
        }

        var maintenanceTypes = new List<string>();
        var maintenanceKeywords = new[] { "maintenance", "inspection", "repair", "upgrade", "replacement", "outage", "shutdown" };

        foreach (var keyword in maintenanceKeywords)
        {
            if (notice.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                maintenanceTypes.Add(keyword);
            }
        }

        if (maintenanceTypes.Any())
        {
            notice.AdditionalProperties["MaintenanceTypes"] = string.Join(", ", maintenanceTypes.Distinct());
        }
    }
}