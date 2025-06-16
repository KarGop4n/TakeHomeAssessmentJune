using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;
using System.Net;
using System.Net.Http;

namespace TradingAlertSystem.Infrastructure.Providers;

public class TetcoPipelineDataProvider : IPipelineDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TetcoPipelineDataProvider> _logger;
    private readonly List<string> _categories = new() { "Outage", "NonCritical", "Critical" };

    // Static fields to prevent rapid requests across all instances
    private static readonly SemaphoreSlim _globalThrottle = new(1, 1);
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private static readonly TimeSpan _minimumRequestInterval = TimeSpan.FromSeconds(15); 
    private const int MAX_DETAIL_PAGES_PER_CATEGORY = 5; // Only fetch top 5 most critical

    private readonly string[] _relevantNoticeTypes =
    {
        "Operational Flow Order",
        "Capacity Constraint",
        "Computer System Status", 
        "Planned Service Outage"
    };

    public TetcoPipelineDataProvider(HttpClient httpClient, ILogger<TetcoPipelineDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string PipelineName => "TETCO Pipeline";

    public async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
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
                    _logger.LogInformation("Waiting additional 10 seconds between TETCO categories...");
                    await Task.Delay(10000, cancellationToken);
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
            var testUrl = "https://infopost.enbridge.com/infopost/NoticesList.asp?pipe=TE&type=CRI";

            var response = await _httpClient.GetAsync(testUrl, cancellationToken);

            var isAvailable = response.IsSuccessStatusCode;

            if (!isAvailable)
            {
                _logger.LogWarning("TETCO endpoint returned {StatusCode}", response.StatusCode);
            }
            else
            {
                _logger.LogDebug("TETCO endpoint is reachable");
            }

            return isAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking TETCO availability");
            return false;
        }
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(string category, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();

        var typeParam = category switch
        {
            "Critical" => "CRI",
            "NonCritical" => "NON",
            "Outage" => "OUT",
            _ => "CRI"
        };

        var url = $"https://infopost.enbridge.com/infopost/NoticesList.asp?pipe=TE&type={typeParam}";

        _logger.LogInformation("Fetching {Category} with FRESH SESSION to bypass tracking: {Url}", category, url);

        try
        {
            await _globalThrottle.WaitAsync(cancellationToken);
            try
            {
                var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                if (timeSinceLastRequest < _minimumRequestInterval)
                {
                    var delayNeeded = _minimumRequestInterval - timeSinceLastRequest;
                    _logger.LogInformation("TETCO anti-bot throttling: waiting {Delay:F0} seconds before fresh session request", delayNeeded.TotalSeconds);
                    await Task.Delay(delayNeeded, cancellationToken);
                }

                _lastRequestTime = DateTime.UtcNow;
            }
            finally
            {
                _globalThrottle.Release();
            }

            // Create a fresh http client for each page to avoid bot detection
            using var freshClient = CreateFreshHttpClient();
            var response = await freshClient.GetAsync(url, cancellationToken);

            _logger.LogInformation("Fresh session response - Status: {StatusCode}, Headers: {Headers}",
                response.StatusCode, string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}")));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HTTP request failed with fresh session: {StatusCode} - {ReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                return notices;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("Empty HTML response for {Category} notices with fresh session", category);
                return notices;
            }

            // Check if we got the fallback page instead of real content
            var hasTable = html.Contains("<table", StringComparison.OrdinalIgnoreCase);
            var hasNoticeType = html.Contains("Notice Type", StringComparison.OrdinalIgnoreCase);

            // Special handling for Outage category 
            if (category == "Outage")
            {
                if (hasTable && (hasNoticeType || html.Contains("outage", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("SUCCESS: Outage page appears legitimate despite smaller size: {Length} characters", html.Length);
                }
                else if (html.Length < 5000)
                {
                    _logger.LogWarning("TETCO outage page too small and lacks expected content: {Length} characters", html.Length);
                    _logger.LogDebug("Outage page preview: {Preview}", html.Substring(0, Math.Min(500, html.Length)));
                    return notices;
                }
            }
            else
            {
                if (html.Length < 25000 || !hasNoticeType)
                {
                    _logger.LogWarning("TETCO anti-bot detection triggered for {Category}: {Length} characters", category, html.Length);
                    return notices;
                }
            }

            _logger.LogInformation("SUCCESS: Fresh session bypassed anti-bot for {Category}: {Length} characters", category, html.Length);

            var mainTableNotices = await ParseMainTableAsync(html, category, url, typeParam, cancellationToken);

            if (!mainTableNotices.Any())
            {
                _logger.LogWarning("No notices found in main table for {Category}", category);
                return notices;
            }

            // Only process the most recent notice for outages
            if (category == "Outage")
            {
                var mostRecentOutage = mainTableNotices
                    .OrderByDescending(n => n.NoticeDate)
                    .FirstOrDefault();

                if (mostRecentOutage != null)
                {
                    _logger.LogInformation("Processing most recent outage notice: {NoticeId} from {Date}",
                        mostRecentOutage.Id, mostRecentOutage.NoticeDate);

                    try
                    {
                        await EnrichOutageNoticeWithDetailPage(mostRecentOutage, cancellationToken);
                        notices.Add(mostRecentOutage);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing outage notice {NoticeId}", mostRecentOutage.Id);
                        notices.Add(mostRecentOutage);
                    }
                }

                return notices;
            }

            var relevantNotices = mainTableNotices.Where(IsRelevantNotice).ToList();

            _logger.LogInformation("Filtered {RelevantCount} relevant notices from {TotalCount} total notices",
                relevantNotices.Count, mainTableNotices.Count);

            var tradingCriticalNotices = relevantNotices
                .Where(IsTradingCritical)
                .OrderByDescending(n => n.NoticeDate) // Most recent first
                .Take(MAX_DETAIL_PAGES_PER_CATEGORY) // Limit to top 5 per category
                .ToList();

            _logger.LogInformation("Selected {CriticalCount} trading-critical notices for detail enrichment (limit: {Limit})",
                tradingCriticalNotices.Count, MAX_DETAIL_PAGES_PER_CATEGORY);

            foreach (var notice in tradingCriticalNotices)
            {
                try
                {
                    await EnrichNoticeWithDetailPage(notice, cancellationToken);
                    notices.Add(notice);

                    await Task.Delay(5000, cancellationToken); 
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching detail page for notice {NoticeId}", notice.Id);
                    notices.Add(notice);
                }
            }

            var remainingNotices = relevantNotices.Except(tradingCriticalNotices).ToList();
            foreach (var notice in remainingNotices)
            {
                notice.AdditionalProperties["DetailEnrichment"] = "Skipped for speed";
                notices.Add(notice);
            }

            _logger.LogInformation("Added {RemainingCount} additional notices without detail enrichment for speed",
                remainingNotices.Count);

            _logger.LogInformation("TETCO {Category} processing complete: {EnrichedCount} enriched, {BasicCount} basic, {TotalCount} total",
                category, tradingCriticalNotices.Count, remainingNotices.Count, notices.Count);

            return notices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Category} notices with fresh session from {Url}", category, url);
            return notices;
        }
    }

    private bool IsTradingCritical(PipelineNotice notice)
    {
        var title = notice.Title.ToLowerInvariant();
        var noticeType = notice.NoticeType.ToLowerInvariant();

        var criticalKeywords = new[]
        {
        "force majeure",
        "emergency",
        "unplanned",
        "curtailment",
        "henry hub",
        "louisiana",
        "gulf coast"
    };

        var hasCriticalKeywords = criticalKeywords.Any(keyword =>
            title.Contains(keyword) || noticeType.Contains(keyword));

        if ((noticeType.Contains("capacity constraint") || noticeType.Contains("operational flow order")))
        {
            var isVeryRecent = notice.IsWithinDays(1);
            var hasLocationKeyword = criticalKeywords.Any(keyword => title.Contains(keyword));
            return isVeryRecent && hasLocationKeyword;
        }

        return hasCriticalKeywords || notice.IsWithinDays(1);
    }

    private async Task<List<PipelineNotice>> ParseMainTableAsync(string html, string category, string sourceUrl, string typeParam, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var dataRows = doc.DocumentNode.SelectNodes("//tr[contains(@class,'odd') or contains(@class,'even')]");

        if (dataRows == null)
        {
            _logger.LogWarning("No data rows found with odd/even classes for {Category}", category);
            return notices;
        }

        _logger.LogInformation("Found {RowCount} data rows in main table for {Category}", dataRows.Count, category);

        foreach (var row in dataRows)
        {
            try
            {
                var notice = ParseTableRow(row, typeParam, sourceUrl);
                if (notice != null)
                {
                    notices.Add(notice);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing table row for {Category}", category);
            }
        }

        return notices;
    }

    private PipelineNotice? ParseTableRow(HtmlNode row, string category, string sourceUrl)
    {
        var cells = row.SelectNodes(".//td");
        if (cells == null || cells.Count < 7) 
        {
            _logger.LogInformation("Skipping row with {CellCount} cells (need 7)", cells?.Count ?? 0);
            return null;
        }

        var notice = new PipelineNotice
        {
            PipelineName = PipelineName,
            AdditionalProperties = { ["Category"] = category }
        };

        notice.NoticeType = cells[0].InnerText?.Trim() ?? string.Empty;

        if (DateTime.TryParse(cells[1].InnerText?.Trim(), out var postedDate))
        {
            notice.NoticeDate = postedDate;
        }

        if (DateTime.TryParse(cells[2].InnerText?.Trim(), out var effectiveDate))
        {
            notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
        }

        if (DateTime.TryParse(cells[3].InnerText?.Trim(), out var endDate))
        {
            notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
        }

        var noticeId = cells[4].InnerText?.Trim();
        _logger.LogInformation("Parsing row: NoticeId='{NoticeId}', Category='{Category}'", noticeId, category);

        if (!string.IsNullOrEmpty(noticeId))
        {
            notice.Id = noticeId;
            notice.AdditionalProperties["NoticeId"] = noticeId;

            notice.SourceUrl = $"https://infopost.enbridge.com/infopost/NoticeListDetail.asp?strKey1={noticeId}&type={category}&Embed=2&pipe=TE";

            _logger.LogInformation("Constructed detail URL for notice {NoticeId}: {Url}", noticeId, notice.SourceUrl);
        }
        else
        {
            notice.Id = Guid.NewGuid().ToString();
            notice.SourceUrl = sourceUrl; 
            _logger.LogWarning("No NoticeId found, using fallback URL for notice {NoticeId}", notice.Id);
        }

        notice.Title = cells[5].InnerText?.Trim() ?? string.Empty;
        notice.Description = notice.Title;

        if (cells.Count > 6)
        {
            var responseDate = cells[6].InnerText?.Trim();
            if (!string.IsNullOrEmpty(responseDate) && DateTime.TryParse(responseDate, out var respDate))
            {
                notice.AdditionalProperties["ResponseDate"] = respDate.ToString("yyyy-MM-dd HH:mm");
            }
        }

        _logger.LogInformation("Successfully parsed notice {NoticeId}: Type='{NoticeType}', Title='{Title}', URL='{Url}'",
            notice.Id, notice.NoticeType,
            notice.Title.Length > 50 ? notice.Title.Substring(0, 50) + "..." : notice.Title,
            notice.SourceUrl);

        return notice;
    }

    private bool IsRelevantNotice(PipelineNotice notice)
    {
        var isRelevantType = _relevantNoticeTypes.Any(type =>
            notice.NoticeType.Contains(type, StringComparison.OrdinalIgnoreCase));

        var hasRelevantKeywords = ContainsRelevantKeywords(notice.Title) ||
                                 ContainsRelevantKeywords(notice.Description);

        return isRelevantType || hasRelevantKeywords;
    }

    private bool ContainsRelevantKeywords(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var keywords = new[]
        {
            "outage", "force majeure", "curtailment", "constraint",
            "emergency", "unplanned", "louisiana", "henry hub"
        };

        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnrichOutageNoticeWithDetailPage(PipelineNotice notice, CancellationToken cancellationToken)
    {
        _logger.LogInformation("STARTING EnrichOutageNoticeWithDetailPage for notice {NoticeId}", notice.Id);

        if (string.IsNullOrEmpty(notice.SourceUrl))
        {
            _logger.LogWarning("No detail URL available for outage notice {NoticeId}", notice.Id);
            return;
        }

        try
        {
            _logger.LogInformation("About to fetch outage detail page for notice {NoticeId}: {Url}", notice.Id, notice.SourceUrl);

            await _globalThrottle.WaitAsync(cancellationToken);
            try
            {
                var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                if (timeSinceLastRequest < _minimumRequestInterval)
                {
                    var delayNeeded = _minimumRequestInterval - timeSinceLastRequest;
                    _logger.LogInformation("TETCO detail page throttling: waiting {Delay:F0} seconds", delayNeeded.TotalSeconds);
                    await Task.Delay(delayNeeded, cancellationToken);
                }

                _lastRequestTime = DateTime.UtcNow;
            }
            finally
            {
                _globalThrottle.Release();
            }

            _logger.LogInformation("Creating completely fresh HTTP client for outage detail page");
            using var freshDetailClient = CreateFreshHttpClient();

            _logger.LogInformation("Fetching outage detail page with brand new fresh client for notice {NoticeId}: {Url}", notice.Id, notice.SourceUrl);

            var response = await freshDetailClient.GetAsync(notice.SourceUrl, cancellationToken);
            _logger.LogInformation("Detail page response for notice {NoticeId}: Status={StatusCode}, Length={Length}",
                notice.Id, response.StatusCode, response.Content.Headers.ContentLength);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch outage detail page for notice {NoticeId}: {StatusCode}",
                    notice.Id, response.StatusCode);
                return;
            }

            var detailHtml = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("📄 Retrieved detail HTML for notice {NoticeId}: {Length} characters", notice.Id, detailHtml.Length);

            var securityIndicators = new[]
            {
            "Request Blocked",
            "Enbridge Security policy",
            "security policy",
            "blocked",
            "access denied",
            "unauthorized",
            "forbidden"
             };

            var hasSecurityBlock = securityIndicators.Any(indicator =>
                detailHtml.Contains(indicator, StringComparison.OrdinalIgnoreCase));

            var isTooSmall = detailHtml.Length < 8000; // Reduced threshold for outage pages

            var hasRealContent = detailHtml.Contains("Notice Text", StringComparison.OrdinalIgnoreCase) ||
                                detailHtml.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ||
                                detailHtml.Contains("outage", StringComparison.OrdinalIgnoreCase);

            if (hasSecurityBlock || (isTooSmall && !hasRealContent))
            {
                _logger.LogError("DETAIL PAGE BLOCKED by Enbridge security for notice {NoticeId}. " +
                               "HTML length: {Length}, SecurityBlock: {SecurityBlock}, HasContent: {HasContent}",
                    notice.Id, detailHtml.Length, hasSecurityBlock, hasRealContent);

                var blockedSample = detailHtml.Length > 1000 ? detailHtml.Substring(0, 1000) + "..." : detailHtml;
                _logger.LogError("Blocked content sample: {Sample}", blockedSample);

                return;
            }

            _logger.LogInformation("SUCCESS: Fresh detail client bypassed security for notice {NoticeId}: {Length} characters",
                notice.Id, detailHtml.Length);

            var htmlSample = detailHtml.Length > 1000 ? detailHtml.Substring(0, 1000) + "..." : detailHtml;
            _logger.LogInformation("Detail HTML sample for notice {NoticeId}: {Sample}", notice.Id, htmlSample);

            var cleanText = System.Text.RegularExpressions.Regex.Replace(detailHtml, @"<[^>]+>", " ");
            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"\s+", " ").Trim();

            notice.Description = ExtractOutageDescription(cleanText);
            _logger.LogInformation("Updated description for notice {NoticeId}: {Description}", notice.Id,
                notice.Description.Length > 100 ? notice.Description.Substring(0, 100) + "..." : notice.Description);

            _logger.LogInformation("About to call ExtractOutageDates for notice {NoticeId}", notice.Id);
            ExtractOutageDates(notice, detailHtml);
            _logger.LogInformation("Completed ExtractOutageDates for notice {NoticeId}", notice.Id);

            ExtractOutageCapacityImpacts(notice, cleanText);
            ExtractOutageLocations(notice, cleanText);
            ExtractOutagePipelineSegments(notice, cleanText);
            ExtractOutageDurations(notice, cleanText);

            notice.AdditionalProperties["TradingKeywords"] = "planned outage,capacity impact,scheduled maintenance,pipeline integrity";
            notice.AdditionalProperties["OutageType"] = "Planned Service Outage";

            if (notice.VolumeImpactMmbtu.HasValue && notice.VolumeImpactMmbtu.Value > 100000)
            {
                notice.AdditionalProperties["TradingPriority"] = "HIGH";
            }
            else if (notice.VolumeImpactMmbtu.HasValue && notice.VolumeImpactMmbtu.Value > 50000)
            {
                notice.AdditionalProperties["TradingPriority"] = "MEDIUM";
            }

            _logger.LogInformation("Successfully enriched outage notice {NoticeId} with comprehensive trading data: " +
                                 "{MaintenanceDates} dates, {Capacity} Dth/d impact",
                notice.Id,
                notice.AdditionalProperties.GetValueOrDefault("MaintenanceDates", "Unknown"),
                notice.VolumeImpactMmbtu?.ToString("N0") ?? "Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching outage notice {NoticeId} with detail page using completely fresh client", notice.Id);
        }
    }

    private string ExtractOutageDescription(string cleanText)
    {
        var noticeTextIndex = cleanText.IndexOf("Notice Text:", StringComparison.OrdinalIgnoreCase);
        if (noticeTextIndex >= 0)
        {
            var remaining = cleanText.Substring(noticeTextIndex);

            var description = remaining.Length > 800 ? remaining.Substring(0, 800).Trim() : remaining.Trim();

            description = description.Replace("Notice Text:", "").Trim();
            return description;
        }

        var outageIndex = cleanText.IndexOf("major outages", StringComparison.OrdinalIgnoreCase);
        if (outageIndex >= 0)
        {
            var remaining = cleanText.Substring(outageIndex);
            return remaining.Length > 600 ? remaining.Substring(0, 600).Trim() : remaining.Trim();
        }

        return cleanText.Length > 500 ? cleanText.Substring(0, 500).Trim() : cleanText;
    }

    private void ExtractOutageDates(PipelineNotice notice, string htmlContent)
    {
        var maintenanceDates = new List<string>();

        _logger.LogInformation("STARTING date extraction for notice {NoticeId}, HTML length: {Length}", notice.Id, htmlContent?.Length ?? 0);

        try
        {
            if (string.IsNullOrEmpty(htmlContent))
            {
                _logger.LogWarning("HTML content is null or empty for notice {NoticeId}", notice.Id);
                return;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            _logger.LogInformation("Loaded HTML document for notice {NoticeId}", notice.Id);

            var yellowSpans = doc.DocumentNode.SelectNodes("//span[contains(@style,'background: yellow')]");
            _logger.LogInformation("Found {Count} yellow highlighted spans for notice {NoticeId}", yellowSpans?.Count ?? 0, notice.Id);

            if (yellowSpans != null)
            {
                foreach (var span in yellowSpans)
                {
                    var spanText = span.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(spanText))
                    {
                        _logger.LogInformation("Processing yellow span: '{SpanText}' for notice {NoticeId}", spanText, notice.Id);
                        ExtractDatesFromText(spanText, maintenanceDates, notice.Id);
                    }
                }
            }

            if (!maintenanceDates.Any())
            {
                _logger.LogInformation("No dates found in yellow spans, trying clean text approach for notice {NoticeId}", notice.Id);

                var cleanText = System.Text.RegularExpressions.Regex.Replace(htmlContent, @"<[^>]+>", " ");
                cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"\s+", " ").Trim();

                _logger.LogInformation("Clean text length: {Length} for notice {NoticeId}", cleanText.Length, notice.Id);

                var sampleText = cleanText.Length > 500 ? cleanText.Substring(0, 500) + "..." : cleanText;
                _logger.LogInformation("Clean text sample for notice {NoticeId}: {Sample}", notice.Id, sampleText);

                var dateMatches = System.Text.RegularExpressions.Regex.Matches(cleanText,
                    @"\b(\w+\s+\d{1,2},\s+\d{4})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                _logger.LogInformation("Found {Count} date matches in clean text for notice {NoticeId}", dateMatches.Count, notice.Id);

                foreach (System.Text.RegularExpressions.Match match in dateMatches)
                {
                    _logger.LogInformation("Date match: '{DateText}' for notice {NoticeId}", match.Groups[1].Value, notice.Id);

                    if (DateTime.TryParse(match.Groups[1].Value, out var date) &&
                        date > DateTime.Now.AddDays(-30) && date < DateTime.Now.AddYears(2))
                    {
                        var dateString = date.ToString("yyyy-MM-dd");
                        if (!maintenanceDates.Contains(dateString))
                        {
                            maintenanceDates.Add(dateString);
                            _logger.LogInformation("Added date from clean text: {Date} for notice {NoticeId}", dateString, notice.Id);
                        }
                    }
                }
            }

            if (maintenanceDates.Any())
            {
                notice.AdditionalProperties["MaintenanceDates"] = string.Join(", ", maintenanceDates.Distinct().OrderBy(d => d));
                _logger.LogInformation("Successfully extracted {Count} maintenance dates for notice {NoticeId}: {Dates}",
                    maintenanceDates.Count, notice.Id, string.Join(", ", maintenanceDates));
            }
            else
            {
                _logger.LogWarning("No maintenance dates found for notice {NoticeId}", notice.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting dates for notice {NoticeId}", notice.Id);
        }
    }



    private void ExtractDatesFromText(string text, List<string> maintenanceDates, string noticeId)
    {
        _logger.LogInformation("ExtractDatesFromText called for notice {NoticeId} with text: '{Text}'", noticeId, text.Length > 200 ? text.Substring(0, 200) + "..." : text);

        var datePatterns = new[]
        {
        @"\b(\w+\s+\d{1,2},\s+\d{4})\b",
        
        @"\b(\w+\s+\d{1,2})\s*-\s*(\d{1,2},\s+\d{4})\b"
    };

        foreach (var pattern in datePatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            _logger.LogInformation("Pattern '{Pattern}' found {Count} matches for notice {NoticeId}", pattern, matches.Count, noticeId);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (pattern.Contains("(\\w+\\s+\\d{1,2})\\s*-\\s*(\\d{1,2},\\s+\\d{4})"))
                {
                    var monthDay1 = match.Groups[1].Value;
                    var day2AndYear = match.Groups[2].Value;

                    var monthMatch = System.Text.RegularExpressions.Regex.Match(monthDay1, @"(\w+)");
                    if (monthMatch.Success)
                    {
                        var month = monthMatch.Groups[1].Value;
                        var startDate = $"{monthDay1}, {day2AndYear.Split(',')[1].Trim()}";
                        var endDate = $"{month} {day2AndYear}";

                        _logger.LogInformation("Range dates for notice {NoticeId}: '{StartDate}' and '{EndDate}'", noticeId, startDate, endDate);
                        TryAddDate(startDate, maintenanceDates, noticeId);
                        TryAddDate(endDate, maintenanceDates, noticeId);
                    }
                }
                else
                {
                    _logger.LogInformation("Standard date for notice {NoticeId}: '{Date}'", noticeId, match.Groups[1].Value);
                    TryAddDate(match.Groups[1].Value, maintenanceDates, noticeId);
                }
            }
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\w+\s+\d{1,2}(?:,\s*\w+\s+\d{1,2})*,\s+\d{4}"))
        {
            _logger.LogInformation("Found multi-date format for notice {NoticeId}", noticeId);

            var yearMatch = System.Text.RegularExpressions.Regex.Match(text, @",\s+(\d{4})$");
            if (yearMatch.Success)
            {
                var year = yearMatch.Groups[1].Value;
                var monthDayMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\b(\w+\s+\d{1,2})\b");

                _logger.LogInformation("Multi-date year: {Year}, found {Count} month-day matches for notice {NoticeId}", year, monthDayMatches.Count, noticeId);

                foreach (System.Text.RegularExpressions.Match monthDayMatch in monthDayMatches)
                {
                    var fullDate = $"{monthDayMatch.Groups[1].Value}, {year}";
                    _logger.LogInformation("Multi-date component for notice {NoticeId}: '{Date}'", noticeId, fullDate);
                    TryAddDate(fullDate, maintenanceDates, noticeId);
                }
            }
        }
    }

    private void TryAddDate(string dateText, List<string> maintenanceDates, string noticeId)
    {
        _logger.LogInformation("TryAddDate called for notice {NoticeId} with '{DateText}'", noticeId, dateText);

        if (DateTime.TryParse(dateText, out var date) &&
            date > DateTime.Now.AddDays(-30) && date < DateTime.Now.AddYears(2))
        {
            var dateString = date.ToString("yyyy-MM-dd");
            if (!maintenanceDates.Contains(dateString))
            {
                maintenanceDates.Add(dateString);
                _logger.LogInformation("Successfully added maintenance date: {Date} from text '{DateText}' for notice {NoticeId}", dateString, dateText, noticeId);
            }
            else
            {
                _logger.LogInformation("Date {Date} already exists for notice {NoticeId}", dateString, noticeId);
            }
        }
        else
        {
            _logger.LogInformation("Failed to parse or validate date '{DateText}' for notice {NoticeId}", dateText, noticeId);
        }
    }

    private void ExtractOutageCapacityImpacts(PipelineNotice notice, string text)
    {
        var capacityRestrictions = new List<string>();

        var capacityPatterns = new[]
        {
        @"capacity.*?(?:through|at)\s+([^.]+?).*?limited to.*?([\d,]+)\s*(Dth/d|DTH/d|MMBtu/d|MCF/d)",
        
        @"(MR\s+\d+|meter \d+).*?limited to.*?([\d,]+)\s*(Dth/d|DTH/d|MMBtu/d|MCF/d)",
        
        @"limited to.*?([\d,]+)\s*(Dth/d|DTH/d|MMBtu/d|MCF/d)",
        
        @"(.*?)will be unavailable for flow",
        
        @"for.*?(approximately \d+.*?(?:hour|minute)).*?limited to.*?([\d,]+)\s*(Dth/d|DTH/d|MMBtu/d|MCF/d)"
    };

        foreach (var pattern in capacityPatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    var location = match.Groups[1].Value.Trim();
                    var volumeText = match.Groups[2].Value.Replace(",", "");
                    var unit = match.Groups.Count > 3 ? match.Groups[3].Value : "Dth/d";

                    if (decimal.TryParse(volumeText, out var volume))
                    {
                        var volumeMmbtu = unit.Contains("Dth", StringComparison.OrdinalIgnoreCase) ? volume : volume;

                        if (notice.VolumeImpactMmbtu == null || volume > notice.VolumeImpactMmbtu)
                        {
                            notice.VolumeImpactMmbtu = volumeMmbtu;
                            notice.VolumeUnit = "Dth/d";
                        }

                        capacityRestrictions.Add($"{location}: {volume:N0} {unit}");

                        _logger.LogInformation("Extracted capacity impact for {NoticeId}: {Location} limited to {Volume} {Unit}",
                            notice.Id, location, volume, unit);
                    }
                }
            }
        }

        if (capacityRestrictions.Any())
        {
            notice.AdditionalProperties["CapacityRestrictions"] = string.Join("; ", capacityRestrictions);
        }
    }

    private void ExtractOutageLocations(PipelineNotice notice, string text)
    {
        var locations = new List<string>();
        var pipelineSegments = new List<string>();
        var meterNumbers = new List<string>();

        var locationPatterns = new[]
        {
        @"Access Area\s*-\s*([^:]+?):",
        
        @"([A-Z][a-z]+\s+City)\s*(?:-|to)\s*([A-Z][a-z]+)(?:\s*-\s*([A-Z][a-z]+))?",
        
        @"(\d+[""]\s*Line\s+\d+)",
        
        @"([A-Z\s]+\s+COUNTY,\s+TX)",
        @"(TEXAS|LOUISIANA|OKLAHOMA|ARKANSAS|MISSISSIPPI)",
        
        @"(MR\s+\d+)",
        @"(\d{5,6})\s*-\s*([^,\n]+)",
        
        @"([A-Z][a-z]+)\s+to\s+([A-Z][a-z]+)",
    };

        foreach (var pattern in locationPatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                for (int i = 1; i < match.Groups.Count; i++)
                {
                    var location = match.Groups[i].Value.Trim();
                    if (!string.IsNullOrEmpty(location) && location.Length > 2)
                    {
                        if (location.Contains("Line"))
                        {
                            pipelineSegments.Add(location);
                        }
                        else if (location.StartsWith("MR "))
                        {
                            meterNumbers.Add(location);
                        }
                        else if (location.Contains("COUNTY") || location.Length > 8)
                        {
                            locations.Add(location);
                        }
                    }
                }
            }
        }

        if (locations.Any())
        {
            notice.Location = locations.First();
        }

        if (locations.Any())
        {
            notice.AdditionalProperties["OutageLocations"] = string.Join("; ", locations.Distinct());
        }

        if (pipelineSegments.Any())
        {
            notice.AdditionalProperties["PipelineSegments"] = string.Join("; ", pipelineSegments.Distinct());
        }

        if (meterNumbers.Any())
        {
            notice.AdditionalProperties["AffectedMeters"] = string.Join("; ", meterNumbers.Distinct());
        }

        _logger.LogInformation("Extracted location data for {NoticeId}: {LocationCount} locations, {SegmentCount} segments, {MeterCount} meters",
            notice.Id, locations.Count, pipelineSegments.Count, meterNumbers.Count);
    }

    private void ExtractOutageDurations(PipelineNotice notice, string text)
    {
        var durations = new List<string>();

        var durationPatterns = new[]
        {
        @"for approximately (\d+)\s*(hour|minute)s?",
        @"for the (?:first|last)\s*(\d+-?\d*)\s*(hour|minute)s?",
        @"\(~(\d+)\s*(hour|minute)s?\)",
        @"approximately (\d+)\s*(hour|minute)s?",
        @"(\d+)\s*(hour|minute)s?\s*during"
    };

        foreach (var pattern in durationPatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var duration = $"{match.Groups[1].Value} {match.Groups[2].Value}s";
                durations.Add(duration);
            }
        }

        if (durations.Any())
        {
            notice.AdditionalProperties["OutageDurations"] = string.Join("; ", durations.Distinct());
            _logger.LogInformation("Extracted {Count} duration estimates for notice {NoticeId}: {Durations}",
                durations.Count, notice.Id, string.Join(", ", durations));
        }
    }

    private void ExtractOutagePipelineSegments(PipelineNotice notice, string text)
    {
        var segmentPatterns = new[]
        {
            @"(Line \d+)", // "Line 17"
            @"(\d+[""]\s*Line \d+)", // "24" Line 17"
            @"(Station \d+)", // "Station 123"
            @"(Compressor \w+)", // "Compressor Station"
            @"(Meter \d+)", // "Meter 456"
            @"(Valve \d+)" // "Valve 789"
        };

        var segments = new List<string>();

        foreach (var pattern in segmentPatterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                segments.Add(match.Groups[1].Value);
            }
        }

        if (segments.Any())
        {
            notice.AdditionalProperties["PipelineSegments"] = string.Join(",", segments.Distinct());

            _logger.LogDebug("Extracted pipeline segments for notice {NoticeId}: {Segments}",
                notice.Id, string.Join(", ", segments));
        }
    }

    private HttpClient CreateFreshHttpClient()
    {
        var handler = new HttpClientHandler()
        {
            CookieContainer = new CookieContainer(), 
            UseCookies = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All 
        };

        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(3);

        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

        return client;
    }

    private async Task EnrichNoticeWithDetailPage(PipelineNotice notice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(notice.SourceUrl))
        {
            _logger.LogDebug("No detail URL available for notice {NoticeId}", notice.Id);
            return;
        }

        try
        {
            _logger.LogDebug("Fetching detail page with completely fresh client for notice {NoticeId}: {Url}", notice.Id, notice.SourceUrl);

            await _globalThrottle.WaitAsync(cancellationToken);
            try
            {
                var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
                if (timeSinceLastRequest < _minimumRequestInterval)
                {
                    var delayNeeded = _minimumRequestInterval - timeSinceLastRequest;
                    _logger.LogDebug("Detail page throttling for notice {NoticeId}: waiting {Delay:F0} seconds", notice.Id, delayNeeded.TotalSeconds);
                    await Task.Delay(delayNeeded, cancellationToken);
                }

                _lastRequestTime = DateTime.UtcNow;
            }
            finally
            {
                _globalThrottle.Release();
            }

            // Fresh client for each detail page
            using var freshDetailClient = CreateFreshHttpClient();

            var response = await freshDetailClient.GetAsync(notice.SourceUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch detail page for notice {NoticeId}: {StatusCode}",
                    notice.Id, response.StatusCode);
                return;
            }

            var detailHtml = await response.Content.ReadAsStringAsync(cancellationToken);

            var hasSecurityBlock = detailHtml.Contains("Request Blocked", StringComparison.OrdinalIgnoreCase) ||
                                  detailHtml.Contains("security policy", StringComparison.OrdinalIgnoreCase);

            if (hasSecurityBlock || detailHtml.Length < 5000)
            {
                _logger.LogWarning("Detail page may be blocked for notice {NoticeId}: Length={Length}, SecurityBlock={SecurityBlock}",
                    notice.Id, detailHtml.Length, hasSecurityBlock);
                return;
            }

            _logger.LogDebug("Successfully retrieved detail page for notice {NoticeId}: {Length} characters", notice.Id, detailHtml.Length);

            var cleanText = System.Text.RegularExpressions.Regex.Replace(detailHtml, @"<[^>]+>", " ");
            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"\s+", " ").Trim();

            if (cleanText.Length > notice.Description.Length)
            {
                var detailSnippet = ExtractDetailSnippet(cleanText);
                if (!string.IsNullOrEmpty(detailSnippet))
                {
                    notice.Description = detailSnippet;
                }
            }

            ExtractVolumeInformation(notice, cleanText);

            ExtractLocationInformation(notice, cleanText);

            ExtractTradingKeywords(notice, cleanText);

        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error enriching notice {NoticeId} with detail page using completely fresh client", notice.Id);
        }
    }

    private string ExtractDetailSnippet(string cleanText)
    {
        var startPatterns = new[] { "Notice:", "Subject:", "Effective:", "Description:" };

        foreach (var pattern in startPatterns)
        {
            var index = cleanText.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var remaining = cleanText.Substring(index);
                return remaining.Length > 500 ? remaining.Substring(0, 500).Trim() : remaining.Trim();
            }
        }

        return cleanText.Length > 500 ? cleanText.Substring(0, 500).Trim() : cleanText.Trim();
    }

    private void ExtractVolumeInformation(PipelineNotice notice, string text)
    {
        var volumePatterns = new[]
        {
            @"([\d,\.]+)\s*(MMBtu|mmbtu|MMBTU)",
            @"([\d,\.]+)\s*(Dth|dth|DTH)",
            @"([\d,\.]+)\s*(MCF|mcf|Mcf)",
            @"([\d,\.]+)\s*(BCF|bcf|Bcf)",
            @"capacity.*?([\d,\.]+)",
            @"volume.*?([\d,\.]+)",
            @"reduction.*?([\d,\.]+)"
        };

        foreach (var pattern in volumePatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var volume))
            {
                notice.VolumeImpactMmbtu = volume;
                notice.VolumeUnit = match.Groups.Count > 2 ? match.Groups[2].Value : "MMBTU";
                _logger.LogDebug("Extracted volume for notice {NoticeId}: {Volume} {Unit}",
                    notice.Id, volume, notice.VolumeUnit);
                break;
            }
        }
    }

    private void ExtractLocationInformation(PipelineNotice notice, string text)
    {
        var locationKeywords = new Dictionary<string, string>
        {
            { "louisiana", "Louisiana" },
            { "henry hub", "Henry Hub" },
            { "texas", "Texas" },
            { "oklahoma", "Oklahoma" },
            { "arkansas", "Arkansas" },
            { "mississippi", "Mississippi" },
            { "alabama", "Alabama" },
            { "gulf coast", "Gulf Coast" },
            { "mainline", "Mainline" },
            { "lateral", "Lateral" }
        };

        foreach (var (keyword, location) in locationKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                notice.Location = location;
                _logger.LogDebug("Extracted location for notice {NoticeId}: {Location}", notice.Id, location);
                break;
            }
        }

        var segmentMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"(Line\s+\w+|Station\s+\d+|Meter\s+\d+|Compressor\s+\w+|Zone\s+\w+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (segmentMatch.Success)
        {
            notice.AdditionalProperties["Segment"] = segmentMatch.Value;
        }
    }

    private void ExtractTradingKeywords(PipelineNotice notice, string text)
    {
        var tradingKeywords = new[]
        {
            "force majeure", "curtailment", "outage", "emergency",
            "unplanned", "capacity reduction", "constraint", "restriction"
        };

        var foundKeywords = new List<string>();

        foreach (var keyword in tradingKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                foundKeywords.Add(keyword);
            }
        }

        if (foundKeywords.Any())
        {
            notice.AdditionalProperties["TradingKeywords"] = string.Join(",", foundKeywords);
            _logger.LogDebug("Found trading keywords for notice {NoticeId}: {Keywords}",
                notice.Id, string.Join(", ", foundKeywords));
        }
    }
}