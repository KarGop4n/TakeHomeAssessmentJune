using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;
using UglyToad.PdfPig;

namespace TradingAlertSystem.Infrastructure.Providers;

public class NGPLPipelineDataProvider : IPipelineDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NGPLPipelineDataProvider> _logger;
    private readonly List<(string Category, string TypeParam)> _categories = new() 
    { 
        ("Critical", "C"), 
        ("NonCritical", "N"),
        ("Outage", "O"),
        ("PlannedServiceOutages", "P")
    };

    public NGPLPipelineDataProvider(HttpClient httpClient, ILogger<NGPLPipelineDataProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string PipelineName => "NGPL Pipeline";

    public async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        var allNotices = new List<PipelineNotice>();

        foreach (var (category, typeParam) in _categories)
        {
            try
            {
                _logger.LogInformation("Fetching {Category} notices from {Pipeline}", category, PipelineName);
                
                List<PipelineNotice> categoryNotices;
                
                if (category == "PlannedServiceOutages")
                {
                    categoryNotices = await GetPlannedServiceOutagesWithPdfAsync(cancellationToken);
                }
                else
                {
                    categoryNotices = await GetNoticesForCategoryAsync(category, typeParam, cancellationToken);
                }
                
                allNotices.AddRange(categoryNotices);
                
                _logger.LogInformation("Retrieved {Count} {Category} notices from {Pipeline}", 
                    categoryNotices.Count, category, PipelineName);
                
                if (category != _categories.Last().Category)
                {
                    await Task.Delay(2000, cancellationToken);
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
            var testUrl = "https://pipeline2.kindermorgan.com/Notices/Notices.aspx?type=C&code=NGPL";
            var response = await _httpClient.GetAsync(testUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking {Pipeline} availability", PipelineName);
            return false;
        }
    }

    private async Task<List<PipelineNotice>> GetPlannedServiceOutagesWithPdfAsync(CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        var outagesUrl = "https://pipeline2.kindermorgan.com/Notices/Notices.aspx?type=P&code=NGPL";
        
        try
        {
            _logger.LogInformation("Fetching planned service outages table for PDF analysis");
            
            var response = await _httpClient.GetAsync(outagesUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var noticeTables = doc.DocumentNode.SelectNodes("//table[@class='igc0f1a379']");
            
            if (noticeTables == null)
            {
                _logger.LogWarning("No planned service outage tables found");
                return notices;
            }

            foreach (var table in noticeTables.Take(3)) // Check first 3 notices
            {
                var row = table.SelectSingleNode(".//tr");
                if (row == null) continue;
                
                var cells = row.SelectNodes(".//td");
                if (cells == null || cells.Count < 7) continue;
                
                var subject = cells[6].InnerText?.Trim() ?? "";
                
                if (subject.Contains("OUTAGE IMPACT REPORT", StringComparison.OrdinalIgnoreCase))
                {
                    var noticeId = cells[5].InnerText?.Trim() ?? "";
                    var dateText = cells[2].InnerText?.Trim() ?? "";
                    
                    _logger.LogInformation("Found outage impact report: Notice {NoticeId}, Date {Date}", noticeId, dateText);
                    
                    var pdfUrl = await GetOutageReportPdfUrlAsync(noticeId, dateText, cancellationToken);
                    
                    if (!string.IsNullOrEmpty(pdfUrl))
                    {
                        var pdfNotices = await ParseOutageReportPdfAsync(pdfUrl, noticeId, cancellationToken);
                        notices.AddRange(pdfNotices);
                        
                        break;
                    }
                }
            }

            return notices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing planned service outages with PDF");
            return notices;
        }
    }

    private async Task<string?> GetOutageReportPdfUrlAsync(string noticeId, string dateText, CancellationToken cancellationToken)
    {
        try
        {
            if (!DateTime.TryParse(dateText, out var noticeDate))
            {
                _logger.LogWarning("Could not parse notice date: {DateText}", dateText);
                return null;
            }
            
            var detailUrl = $"https://pipeline2.kindermorgan.com/Notices/NoticeDetail.aspx?code=NGPL&notc_nbr={noticeId}&date={noticeDate:M/d/yyyy}&subject=&notc_type=19&notc_sub_type=-1&notc_ind=P";
            
            var response = await _httpClient.GetAsync(detailUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            
            var pdfUrlMatch = Regex.Match(html, @"(https://[^""'\s]*NGPL_OutageImpactReport[^""'\s]*\.pdf)", RegexOptions.IgnoreCase);
            
            if (pdfUrlMatch.Success)
            {
                var pdfUrl = pdfUrlMatch.Groups[1].Value;
                _logger.LogInformation("Found outage report PDF: {PdfUrl}", pdfUrl);
                return pdfUrl;
            }
            
            _logger.LogWarning("No outage impact report PDF found in detail page for notice {NoticeId}", noticeId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PDF URL for notice {NoticeId}", noticeId);
            return null;
        }
    }

    private async Task<List<PipelineNotice>> ParseOutageReportPdfAsync(string pdfUrl, string noticeId, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        
        try
        {
            _logger.LogInformation("Downloading and parsing outage report PDF: {PdfUrl}", pdfUrl);
            
            var response = await _httpClient.GetAsync(pdfUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var pdfBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            
            if (pdfBytes.Length < 4 || 
                pdfBytes[0] != 0x25 || pdfBytes[1] != 0x50 || 
                pdfBytes[2] != 0x44 || pdfBytes[3] != 0x46)
            {
                _logger.LogWarning("Downloaded file is not a valid PDF");
                return notices;
            }
            
            _logger.LogInformation("Successfully downloaded PDF: {Size:N0} bytes", pdfBytes.Length);
            
            using var pdfDocument = PdfDocument.Open(pdfBytes);
            var allText = new List<string>();
            
            foreach (var page in pdfDocument.GetPages())
            {
                var pageText = page.Text;
                allText.Add(pageText);
                _logger.LogDebug("Extracted {CharCount} characters from page {PageNum}", pageText.Length, page.Number);
            }
            
            var fullText = string.Join("\n", allText);
            _logger.LogInformation("Extracted {TotalChars} characters from {PageCount} pages", fullText.Length, pdfDocument.NumberOfPages);
            
            var outageData = ParseCapacityRestrictionsFromText(fullText);
            
            foreach (var outage in outageData)
            {
                var capacityNotice = new PipelineNotice
                {
                    Id = $"{noticeId}-{outage.Station.Replace(" ", "").Replace("(", "").Replace(")", "")}",
                    PipelineName = PipelineName,
                    NoticeType = "PLANNED CAPACITY RESTRICTION",
                    Title = $"{outage.Station}: {outage.CapacityPercentage}% Capacity Available",
                    Description = outage.OutageDescription,
                    NoticeDate = DateTime.UtcNow,
                    Location = ExtractLocationFromStation(outage.Station),
                    SourceUrl = pdfUrl,
                    AdditionalProperties = 
                    {
                        ["Category"] = "PlannedServiceOutages",
                        ["ParentNoticeId"] = noticeId,
                        ["Station"] = outage.Station,
                        ["CapacityPercentage"] = outage.CapacityPercentage.ToString(),
                        ["OutageDescription"] = outage.OutageDescription,
                        ["OutageStartDate"] = outage.StartDate?.ToString("yyyy-MM-dd") ?? "",
                        ["OutageEndDate"] = outage.EndDate?.ToString("yyyy-MM-dd") ?? "",
                        ["WorkOrderNumber"] = outage.WorkOrderNumber ?? "",
                        ["TradingPriority"] = DetermineTradingPriority(outage.CapacityPercentage),
                        ["RestrictionType"] = ClassifyRestrictionSeverity(outage.CapacityPercentage)
                    }
                };
                
                if (outage.CapacityPercentage < 100)
                {
                    // Estimate volume impact
                    var capacityReduction = 100 - outage.CapacityPercentage;
                    capacityNotice.VolumeImpactMmbtu = capacityReduction * 10000; // Rough estimate
                    capacityNotice.VolumeUnit = "MMBtu/d estimated impact";
                }
                
                notices.Add(capacityNotice);
                
                _logger.LogInformation("Created capacity restriction notice: {Station} at {Capacity}% capacity", 
                    outage.Station, outage.CapacityPercentage);
            }
            
            if (outageData.Any())
            {
                var summaryNotice = new PipelineNotice
                {
                    Id = $"{noticeId}-SUMMARY",
                    PipelineName = PipelineName,
                    NoticeType = "OUTAGE IMPACT REPORT SUMMARY",
                    Title = $"Weekly Outage Impact Report - {outageData.Count} Capacity Restrictions",
                    Description = $"Summary of planned outages affecting pipeline capacity. {outageData.Count(o => o.CapacityPercentage < 90)} stations with significant restrictions.",
                    NoticeDate = DateTime.UtcNow,
                    SourceUrl = pdfUrl,
                    AdditionalProperties = 
                    {
                        ["Category"] = "PlannedServiceOutages",
                        ["NoticeId"] = noticeId,
                        ["TotalRestrictions"] = outageData.Count.ToString(),
                        ["SignificantRestrictions"] = outageData.Count(o => o.CapacityPercentage < 90).ToString(),
                        ["MajorRestrictions"] = outageData.Count(o => o.CapacityPercentage < 80).ToString(),
                        ["TradingPriority"] = outageData.Any(o => o.CapacityPercentage < 80) ? "HIGH" : "MEDIUM",
                        ["ReportType"] = "Seven Day Forecast and Monthly Outlook"
                    }
                };
                
                notices.Insert(0, summaryNotice); // Add summary first
            }
            
            _logger.LogInformation("Successfully parsed PDF: {NoticeCount} notices created from {OutageCount} capacity restrictions", 
                notices.Count, outageData.Count);
            
            return notices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing outage report PDF: {PdfUrl}", pdfUrl);
            return notices;
        }
    }

    private List<OutageData> ParseCapacityRestrictionsFromText(string text)
    {
        var outages = new List<OutageData>();
        
        try
        {
            // Look for station/segment lines with capacity percentages less than 100%
            var stationPattern = @"(Station\s+\d+[^(]*\([^)]+\))[^\d]*?(\d{1,2})%";
            var matches = Regex.Matches(text, stationPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            
            foreach (Match match in matches)
            {
                var station = match.Groups[1].Value.Trim();
                if (int.TryParse(match.Groups[2].Value, out var capacity) && capacity < 100)
                {
                    var outage = new OutageData
                    {
                        Station = station,
                        CapacityPercentage = capacity
                    };
                    
                    var stationIndex = text.IndexOf(station, StringComparison.OrdinalIgnoreCase);
                    if (stationIndex >= 0)
                    {
                        var contextStart = Math.Max(0, stationIndex);
                        var contextLength = Math.Min(500, text.Length - contextStart);
                        var context = text.Substring(contextStart, contextLength);
                        
                        // Look for work order and description patterns
                        var workOrderMatch = Regex.Match(context, @"(X\d{2}-\d+):\s*([^(]+)(?:\(([^)]+)\))?", RegexOptions.IgnoreCase);
                        if (workOrderMatch.Success)
                        {
                            outage.WorkOrderNumber = workOrderMatch.Groups[1].Value;
                            outage.OutageDescription = workOrderMatch.Groups[2].Value.Trim();
                            
                            var dateRange = workOrderMatch.Groups[3].Value;
                            ParseDateRange(dateRange, outage);
                        }
                        else
                        {
                            var descriptionMatch = Regex.Match(context, @"(?:Pipeline Integrity|Station Maintenance|Cleaning|Combo MFL-C|EMAT)[^(]*(?:\([^)]+\))?", RegexOptions.IgnoreCase);
                            if (descriptionMatch.Success)
                            {
                                outage.OutageDescription = descriptionMatch.Value.Trim();
                            }
                        }
                    }
                    
                    if (string.IsNullOrEmpty(outage.OutageDescription))
                    {
                        outage.OutageDescription = $"Planned maintenance affecting {station}";
                    }
                    
                    outages.Add(outage);
                }
            }
            
            _logger.LogInformation("Parsed {Count} capacity restrictions from PDF text", outages.Count);
            return outages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing capacity restrictions from text");
            return outages;
        }
    }

    private void ParseDateRange(string dateText, OutageData outage)
    {
        if (string.IsNullOrEmpty(dateText)) return;
        
        try
        {
            // Pattern: "6/23/2025 - 6/27/2025"
            var dateRangeMatch = Regex.Match(dateText, @"(\d{1,2}/\d{1,2}/\d{4})\s*-\s*(\d{1,2}/\d{1,2}/\d{4})");
            if (dateRangeMatch.Success)
            {
                if (DateTime.TryParse(dateRangeMatch.Groups[1].Value, out var startDate))
                    outage.StartDate = startDate;
                if (DateTime.TryParse(dateRangeMatch.Groups[2].Value, out var endDate))
                    outage.EndDate = endDate;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing date range: {DateText}", dateText);
        }
    }

    private string ExtractLocationFromStation(string station)
    {
        // Extract segment information and station number
        var segmentMatch = Regex.Match(station, @"segment\s+(\d+)\s+([A-Z]+)", RegexOptions.IgnoreCase);
        if (segmentMatch.Success)
        {
            return $"Segment {segmentMatch.Groups[1].Value} {segmentMatch.Groups[2].Value}";
        }
        
        var stationMatch = Regex.Match(station, @"Station\s+(\d+)", RegexOptions.IgnoreCase);
        if (stationMatch.Success)
        {
            return $"Station {stationMatch.Groups[1].Value}";
        }
        
        return station;
    }

    private string DetermineTradingPriority(int capacityPercentage)
    {
        return capacityPercentage switch
        {
            < 60 => "CRITICAL",
            < 80 => "HIGH", 
            < 95 => "MEDIUM",
            _ => "LOW"
        };
    }

    private string ClassifyRestrictionSeverity(int capacityPercentage)
    {
        return capacityPercentage switch
        {
            < 60 => "Significant restrictions to subscribed capacity",
            < 80 => "Major restrictions to subscribed capacity", 
            < 95 => "Minor restrictions to subscribed capacity",
            _ => "No anticipated impact to subscribed capacity"
        };
    }

    private class OutageData
    {
        public string Station { get; set; } = "";
        public int CapacityPercentage { get; set; }
        public string OutageDescription { get; set; } = "";
        public string? WorkOrderNumber { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

    private async Task<List<PipelineNotice>> GetNoticesForCategoryAsync(string category, string typeParam, CancellationToken cancellationToken)
    {
        var notices = new List<PipelineNotice>();
        var url = $"https://pipeline2.kindermorgan.com/Notices/Notices.aspx?type={typeParam}&code=NGPL";
        
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
            
            if (string.IsNullOrEmpty(htmlContent))
            {
                _logger.LogWarning("Empty HTML response for {Category} notices", category);
                return notices;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            // Target the individual notice tables with class 'igc0f1a379'
            var noticeTables = doc.DocumentNode.SelectNodes("//table[@class='igc0f1a379']");
            
            if (noticeTables == null)
            {
                _logger.LogWarning("No notice tables found for {Category} using Infragistics selector", category);
                return notices;
            }

            _logger.LogInformation("Found {Count} notice tables for {Category}", noticeTables.Count, category);

            foreach (var table in noticeTables)
            {
                try
                {
                    var notice = ParseNoticeFromTable(table, category, url);
                    if (notice != null)
                    {
                        notices.Add(notice);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing individual notice table for {Category}", category);
                }
            }

            return notices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Category} notices from {Url}", category, url);
            return notices;
        }
    }

    private PipelineNotice? ParseNoticeFromTable(HtmlNode table, string category, string sourceUrl)
    {
        var row = table.SelectSingleNode(".//tr");
        if (row == null)
        {
            _logger.LogWarning("No row found in notice table");
            return null;
        }

        var cells = row.SelectNodes(".//td");
        if (cells == null || cells.Count < 7)
        {
            _logger.LogWarning("Insufficient cells in notice row: {CellCount}", cells?.Count ?? 0);
            return null;
        }

        var notice = new PipelineNotice
        {
            PipelineName = PipelineName,
            AdditionalProperties = { ["Category"] = category },
            SourceUrl = sourceUrl
        };

        try
        {
            // Col 1: Primary Notice Type (e.g., "PIPELINE CONDITIONS", "CAPACITY CONSTRAINT")
            // Col 2: Secondary Notice Type (e.g., "CURRENT PIPELINE CONDITIONS", "SEGMENTS")
            // Col 3: Post Date
            // Col 4: Effective Date  
            // Col 5: End Date
            // Col 6: Notice ID
            // Col 7: Subject/Title
            
            var primaryType = cells[0].InnerText?.Trim() ?? "";
            var secondaryType = cells[1].InnerText?.Trim() ?? "";
            
            notice.NoticeType = string.IsNullOrEmpty(secondaryType) || secondaryType == primaryType 
                ? primaryType 
                : $"{primaryType} - {secondaryType}";

            if (DateTime.TryParse(cells[2].InnerText?.Trim(), out var postDate))
            {
                notice.NoticeDate = postDate;
            }

            if (DateTime.TryParse(cells[3].InnerText?.Trim(), out var effectiveDate))
            {
                notice.AdditionalProperties["EffectiveDate"] = effectiveDate.ToString("yyyy-MM-dd HH:mm");
            }

            if (DateTime.TryParse(cells[4].InnerText?.Trim(), out var endDate))
            {
                notice.AdditionalProperties["EndDate"] = endDate.ToString("yyyy-MM-dd HH:mm");
            }

            var noticeId = cells[5].InnerText?.Trim();
            if (!string.IsNullOrEmpty(noticeId))
            {
                notice.Id = noticeId;
                notice.AdditionalProperties["NoticeId"] = noticeId;
            }
            else
            {
                notice.Id = Guid.NewGuid().ToString();
            }

            notice.Title = cells[6].InnerText?.Trim() ?? "";
            notice.Description = notice.Title;

            ExtractLocationFromNotice(notice);
            ExtractVolumeInformation(notice);
            ExtractSegmentInformation(notice);

            return notice;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing notice fields");
            return null;
        }
    }

    private void ExtractLocationFromNotice(PipelineNotice notice)
    {
        var searchText = $"{notice.NoticeType} {notice.Title}".ToLowerInvariant();
        
        var locationKeywords = new Dictionary<string, string>
        {
            { "louisiana", "Louisiana" },
            { "texas", "Texas" },
            { "oklahoma", "Oklahoma" },
            { "kansas", "Kansas" },
            { "arkansas", "Arkansas" },
            { "henry hub", "Henry Hub" },
            { "carthage", "Carthage" },
            { "katy", "Katy" },
            { "henry", "Henry Hub" },
            { "gulf coast", "Gulf Coast" },
            { "segment", "Pipeline Segment" },
            { "compressor", "Compressor Station" }
        };

        foreach (var (keyword, location) in locationKeywords)
        {
            if (searchText.Contains(keyword))
            {
                notice.Location = location;
                break;
            }
        }


        var locationMatch = System.Text.RegularExpressions.Regex.Match(notice.Title,
            @"([A-Z][A-Z\s]+?)\s*\(LOC\s*\d+\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (locationMatch.Success)
        {
            var pointName = locationMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(notice.Location))
            {
                notice.Location = pointName;
            }
            notice.AdditionalProperties["DeliveryPoint"] = pointName;
        }

        // Extract segment information: "SEGMENT 11", "SEGMENT 27 NB"
        var segmentMatch = System.Text.RegularExpressions.Regex.Match(notice.Title,
            @"SEGMENT\s+(\d+(?:\s+[A-Z]+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (segmentMatch.Success)
        {
            notice.AdditionalProperties["Segment"] = segmentMatch.Groups[1].Value.Trim();
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
            @"constraint.*?([\d,\.]+)",
            @"limited.*?to.*?([\d,\.]+)",
            @"restriction.*?([\d,\.]+)"
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

    private void ExtractSegmentInformation(PipelineNotice notice)
    {
        var segments = new List<string>();
        
        var compressorMatches = System.Text.RegularExpressions.Regex.Matches(notice.Title,
            @"CS\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (System.Text.RegularExpressions.Match match in compressorMatches)
        {
            segments.Add($"Compressor Station {match.Groups[1].Value}");
        }

        var segmentMatches = System.Text.RegularExpressions.Regex.Matches(notice.Title,
            @"SEGMENT\s+(\d+(?:\s+[A-Z]+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (System.Text.RegularExpressions.Match match in segmentMatches)
        {
            segments.Add($"Segment {match.Groups[1].Value}");
        }

        if (segments.Any())
        {
            notice.AdditionalProperties["PipelineSegments"] = string.Join(", ", segments.Distinct());
        }

        ClassifyNoticePriority(notice);
    }

    private void ClassifyNoticePriority(PipelineNotice notice)
    {
        var title = notice.Title.ToLowerInvariant();
        var noticeType = notice.NoticeType.ToLowerInvariant();
        
        var highPriorityKeywords = new[] { "force majeure", "emergency", "outage", "curtailment" };
        var mediumPriorityKeywords = new[] { "constraint", "restriction", "maintenance", "capacity" };
        var operationalKeywords = new[] { "current pipeline conditions", "lifted", "prim only" };

        if (highPriorityKeywords.Any(keyword => title.Contains(keyword) || noticeType.Contains(keyword)))
        {
            notice.AdditionalProperties["TradingPriority"] = "HIGH";
        }
        else if (mediumPriorityKeywords.Any(keyword => title.Contains(keyword) || noticeType.Contains(keyword)))
        {
            notice.AdditionalProperties["TradingPriority"] = "MEDIUM";
        }
        else if (operationalKeywords.Any(keyword => title.Contains(keyword) || noticeType.Contains(keyword)))
        {
            notice.AdditionalProperties["TradingPriority"] = "LOW";
        }

        if (noticeType.Contains("capacity constraint"))
        {
            notice.AdditionalProperties["ConstraintType"] = notice.NoticeType.Contains("SEGMENTS") ? "Segment" : "Point";
        }

        if (title.Contains("lifted"))
        {
            notice.AdditionalProperties["NoticeAction"] = "LIFTED";
        }
        else if (title.Contains("prim only"))
        {
            notice.AdditionalProperties["NoticeAction"] = "PRIMARY_ONLY";
        }
    }
}