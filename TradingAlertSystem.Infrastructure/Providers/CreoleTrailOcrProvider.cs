using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

/// <summary>
/// Standalone OCR-based provider for extracting detailed maintenance data from Cheniere base64 images.
/// This is separate from the main trading alert system to avoid dependencies and complexity.
/// Use this for detailed capacity impact analysis and maintenance scheduling.
/// </summary>
public class CreoleTrailOcrProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CreoleTrailOcrProvider> _logger;
    
    private readonly Dictionary<string, int> _pipelineConfigs = new()
    {
        { "CTPL", 200 }, // Creole Trail Pipeline
        { "CCPL", 400 }  // Corpus Christi Pipeline
    };

    public CreoleTrailOcrProvider(HttpClient httpClient, ILogger<CreoleTrailOcrProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string ProviderName => "Creole Trail OCR Maintenance Extractor";

    /// <summary>
    /// Extract detailed maintenance schedules with capacity impacts using OCR
    /// </summary>
    public async Task<List<DetailedMaintenanceSchedule>> GetDetailedMaintenanceSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var allSchedules = new List<DetailedMaintenanceSchedule>();

        foreach (var (pipelineName, tspNo) in _pipelineConfigs)
        {
            try
            {
                _logger.LogInformation("Extracting detailed maintenance data for {Pipeline} via OCR", pipelineName);
                
                var schedule = await ExtractMaintenanceScheduleForPipeline(pipelineName, tspNo, cancellationToken);
                if (schedule != null)
                {
                    allSchedules.Add(schedule);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting maintenance schedule for {Pipeline}", pipelineName);
            }
        }

        return allSchedules;
    }

    /// <summary>
    /// Test OCR functionality and return debug information
    /// </summary>
    public async Task<OcrTestResult> TestOcrExtractionAsync(string pipelineName, CancellationToken cancellationToken = default)
    {
        var result = new OcrTestResult { PipelineName = pipelineName };

        try
        {
            if (!_pipelineConfigs.TryGetValue(pipelineName, out var tspNo))
            {
                result.ErrorMessage = $"Unknown pipeline: {pipelineName}";
                return result;
            }

            // Get the latest planned outage notice
            var latestNotice = await GetLatestPlannedOutageNotice(tspNo, cancellationToken);
            if (latestNotice == null)
            {
                result.ErrorMessage = "No planned outage notices found";
                return result;
            }

            result.NoticeId = latestNotice.NoticeId;
            result.NoticeSubject = latestNotice.Subject;

            // Extract images
            var images = await ExtractImagesFromNotice(tspNo, latestNotice.NoticeId, cancellationToken);
            result.ImagesFound = images.Count;

            if (!images.Any())
            {
                result.ErrorMessage = "No base64 images found in notice";
                return result;
            }

            // Test OCR on first image
            var firstImageBytes = images[0];
            var ocrText = await PerformOcrOnImage(firstImageBytes, $"test_{pipelineName}", cancellationToken);
            
            result.OcrTextLength = ocrText?.Length ?? 0;
            result.OcrSuccessful = !string.IsNullOrEmpty(ocrText);
            result.ContainsMaintenanceData = ContainsMaintenanceTableData(ocrText ?? "");

            if (result.ContainsMaintenanceData)
            {
                var maintenanceItems = ParseMaintenanceItemsFromOcrText(ocrText!, _logger);
                result.MaintenanceItemsExtracted = maintenanceItems.Count;
                result.SampleMaintenanceItems = maintenanceItems.Take(3).ToList();
            }

            result.Success = result.OcrSuccessful && result.ContainsMaintenanceData;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Error testing OCR extraction for {Pipeline}", pipelineName);
        }

        return result;
    }

    private async Task<DetailedMaintenanceSchedule?> ExtractMaintenanceScheduleForPipeline(
        string pipelineName, 
        int tspNo, 
        CancellationToken cancellationToken)
    {
        try
        {
            // Get the latest planned outage notice
            var latestNotice = await GetLatestPlannedOutageNotice(tspNo, cancellationToken);
            if (latestNotice == null)
            {
                _logger.LogWarning("No planned outage notices found for {Pipeline}", pipelineName);
                return null;
            }

            // Extract images from the notice
            var images = await ExtractImagesFromNotice(tspNo, latestNotice.NoticeId, cancellationToken);
            if (!images.Any())
            {
                _logger.LogWarning("No maintenance table images found for {Pipeline}", pipelineName);
                return null;
            }

            var schedule = new DetailedMaintenanceSchedule
            {
                PipelineName = pipelineName,
                NoticeId = latestNotice.NoticeId,
                NoticeSubject = latestNotice.Subject,
                EffectiveDate = latestNotice.EffectiveDate,
                ExtractedAt = DateTime.UtcNow,
                MaintenanceItems = new List<DetailedMaintenanceItem>()
            };

            // Process each image with OCR
            for (int i = 0; i < images.Count; i++)
            {
                try
                {
                    var imageBytes = images[i];
                    var imageName = $"{pipelineName}_{latestNotice.NoticeId}_{i + 1}";
                    
                    _logger.LogInformation("Processing maintenance image {ImageName} for {Pipeline}", imageName, pipelineName);
                    
                    var ocrText = await PerformOcrOnImage(imageBytes, imageName, cancellationToken);
                    
                    if (string.IsNullOrEmpty(ocrText))
                    {
                        _logger.LogWarning("No text extracted from image {ImageName}", imageName);
                        continue;
                    }

                    if (ContainsMaintenanceTableData(ocrText))
                    {
                        var maintenanceItems = ParseMaintenanceItemsFromOcrText(ocrText, _logger);
                        
                        foreach (var item in maintenanceItems)
                        {
                            schedule.MaintenanceItems.Add(new DetailedMaintenanceItem
                            {
                                JobNumber = item.JobNo ?? "",
                                Quarter = item.Quarter ?? "",
                                BeginDate = item.BeginDate,
                                EndDate = item.EndDate,
                                GasDay = item.GasDay ?? "",
                                Duration = item.Duration ?? "",
                                Location = item.Location ?? "",
                                MaintenanceType = item.MaintenanceType ?? "",
                                TransportImpactDths = item.TransportImpactDths,
                                AvailableCapacityDths = item.AvailableCapacityDths,
                                SourceImage = imageName
                            });
                        }
                        
                        _logger.LogInformation("Extracted {Count} maintenance items from image {ImageName}", 
                            maintenanceItems.Count, imageName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing image {ImageIndex} for {Pipeline}", i + 1, pipelineName);
                }
            }

            // Calculate summary statistics
            schedule.TotalMaintenanceJobs = schedule.MaintenanceItems.Count;
            schedule.TotalCapacityImpactDths = schedule.MaintenanceItems
                .Where(m => m.TransportImpactDths.HasValue)
                .Sum(m => m.TransportImpactDths.Value);
            
            var dates = schedule.MaintenanceItems
                .Where(m => m.BeginDate.HasValue)
                .Select(m => m.BeginDate!.Value)
                .OrderBy(d => d)
                .ToList();
            
            if (dates.Any())
            {
                schedule.EarliestMaintenanceDate = dates.First();
                schedule.LatestMaintenanceDate = dates.Last();
            }

            _logger.LogInformation("Extracted complete maintenance schedule for {Pipeline}: {JobCount} jobs, {TotalImpact:N0} Dths total impact", 
                pipelineName, schedule.TotalMaintenanceJobs, schedule.TotalCapacityImpactDths);

            return schedule;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting maintenance schedule for {Pipeline}", pipelineName);
            return null;
        }
    }

    private async Task<PlannedOutageNotice?> GetLatestPlannedOutageNotice(int tspNo, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://lngconnectionapi.cheniere.com/api/Notice/GetNoticesByPageId?tspNo={tspNo}&pageId=11";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(jsonContent) || jsonContent == "[]")
                return null;

            var jsonArray = JsonDocument.Parse(jsonContent).RootElement;
            if (jsonArray.ValueKind != JsonValueKind.Array || jsonArray.GetArrayLength() == 0)
                return null;

            // Get the first (most recent) notice
            var firstNotice = jsonArray[0];
            
            return new PlannedOutageNotice
            {
                NoticeId = firstNotice.GetProperty("noticeId").GetInt32().ToString(),
                Subject = firstNotice.GetProperty("subject").GetString() ?? "",
                EffectiveDate = DateTime.TryParse(firstNotice.GetProperty("effectiveDateTime").GetString(), out var effDate) ? effDate : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching latest planned outage notice for tspNo {TspNo}", tspNo);
            return null;
        }
    }

    private async Task<List<byte[]>> ExtractImagesFromNotice(int tspNo, string noticeId, CancellationToken cancellationToken)
    {
        try
        {
            var detailUrl = $"https://lngconnectionapi.cheniere.com/api/Notice/GetNoticeById?tspNo={tspNo}&noticeId={noticeId}";
            var response = await _httpClient.GetAsync(detailUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonDoc = JsonDocument.Parse(jsonContent);
            var noticeArray = jsonDoc.RootElement;
            
            if (noticeArray.ValueKind != JsonValueKind.Array || noticeArray.GetArrayLength() == 0)
                return new List<byte[]>();
            
            var noticeElement = noticeArray[0];
            if (!noticeElement.TryGetProperty("noticeText", out var noticeTextElement))
                return new List<byte[]>();
            
            var htmlContent = noticeTextElement.GetString() ?? "";
            var base64Images = ExtractBase64ImagesFromHtml(htmlContent);
            
            var imageBytesList = new List<byte[]>();
            foreach (var base64Data in base64Images)
            {
                try
                {
                    var imageBytes = Convert.FromBase64String(base64Data);
                    imageBytesList.Add(imageBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode base64 image");
                }
            }
            
            return imageBytesList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting images from notice {NoticeId}", noticeId);
            return new List<byte[]>();
        }
    }

    private async Task<string> PerformOcrOnImage(byte[] imageBytes, string imageName, CancellationToken cancellationToken)
    {
        var tempImagePath = $"temp_{imageName}.png";
        var outputBasePath = $"temp_{imageName}_output";
        
        try
        {
            // Save image temporarily
            await File.WriteAllBytesAsync(tempImagePath, imageBytes, cancellationToken);
            
            // Run tesseract via command line
            var processInfo = new ProcessStartInfo
            {
                FileName = "tesseract",
                Arguments = $"\"{tempImagePath}\" \"{outputBasePath}\" -c preserve_interword_spaces=1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start tesseract process");
                return string.Empty;
            }

            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogError("Tesseract failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                return string.Empty;
            }

            // Read the output file
            var outputFile = outputBasePath + ".txt";
            if (File.Exists(outputFile))
            {
                var ocrText = await File.ReadAllTextAsync(outputFile, cancellationToken);
                
                // Clean up temporary files
                File.Delete(outputFile);
                
                _logger.LogInformation("OCR extracted {Length} characters from image {ImageName}", 
                    ocrText.Length, imageName);
                
                return ocrText;
            }
            
            _logger.LogWarning("Tesseract output file not found for image {ImageName}", imageName);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing OCR on image {ImageName}", imageName);
            return string.Empty;
        }
        finally
        {
            // Clean up temporary image file
            if (File.Exists(tempImagePath))
            {
                File.Delete(tempImagePath);
            }
        }
    }

    private List<string> ExtractBase64ImagesFromHtml(string htmlContent)
    {
        var base64Images = new List<string>();
        
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);
            
            var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
            if (imgNodes != null)
            {
                foreach (var imgNode in imgNodes)
                {
                    var src = imgNode.GetAttributeValue("src", "");
                    if (src.StartsWith("data:image/png;base64,"))
                    {
                        var base64Data = src.Substring("data:image/png;base64,".Length);
                        base64Images.Add(base64Data);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting base64 images from HTML");
        }
        
        return base64Images;
    }

    private bool ContainsMaintenanceTableData(string ocrText)
    {
        var tableIndicators = new[]
        {
            "Job No", "Quarter", "Begin", "End", "Duration", "Location", 
            "Maintenance", "Transport Impact", "Available Capacity", "Dths",
            "Compressor Station", "Gas Day"
        };
        
        var foundIndicators = tableIndicators.Count(indicator => 
            ocrText.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        
        return foundIndicators >= 5;
    }

    private List<MaintenanceItem> ParseMaintenanceItemsFromOcrText(string ocrText, ILogger logger)
    {
        var items = new List<MaintenanceItem>();
        
        try
        {
            var lines = ocrText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line))
                .ToList();
            
            var jobNumberPattern = @"^\d{4}\.\d+";
            
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, jobNumberPattern))
                {
                    var item = ParseMaintenanceRowFromLine(line, logger);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error parsing maintenance items from OCR text");
        }
        
        return items;
    }

    private MaintenanceItem? ParseMaintenanceRowFromLine(string line, ILogger logger)
    {
        try
        {
            var item = new MaintenanceItem();
            
            // Extract job number
            var jobMatch = Regex.Match(line, @"(\d{4}\.\d+)");
            if (jobMatch.Success)
            {
                item.JobNo = jobMatch.Groups[1].Value;
            }
            
            // Extract quarter
            var quarterMatch = Regex.Match(line, @"(Q[1-4])");
            if (quarterMatch.Success)
            {
                item.Quarter = quarterMatch.Groups[1].Value;
            }
            
            // Extract dates
            var dateMatches = Regex.Matches(line, @"(\d{1,2}/\d{1,2}/\d{4})");
            if (dateMatches.Count >= 2)
            {
                if (DateTime.TryParse(dateMatches[0].Groups[1].Value, out var beginDate))
                    item.BeginDate = beginDate;
                    
                if (DateTime.TryParse(dateMatches[1].Groups[1].Value, out var endDate))
                    item.EndDate = endDate;
            }
            
            // Extract location (look for "Compressor Station" or station names)
            var locationMatch = Regex.Match(line, @"([\w\s]+(?:Compressor Station|Station))");
            if (locationMatch.Success)
            {
                item.Location = locationMatch.Groups[1].Value.Trim();
            }
            
            // Extract maintenance type
            var maintenanceTypes = new[] { "ESD Testing", "Compressor Maintenance", "Header Tie-in", "Testing", "Maintenance" };
            foreach (var type in maintenanceTypes)
            {
                if (line.Contains(type, StringComparison.OrdinalIgnoreCase))
                {
                    item.MaintenanceType = type;
                    break;
                }
            }
            
            // Extract capacity numbers
            var capacityMatches = Regex.Matches(line, @"([\d,]{3,})");
            var capacityNumbers = new List<decimal>();
            
            foreach (Match match in capacityMatches)
            {
                if (decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var capacity))
                {
                    capacityNumbers.Add(capacity);
                }
            }
            
            if (capacityNumbers.Count >= 1)
            {
                item.TransportImpactDths = capacityNumbers[0];
            }
            
            if (capacityNumbers.Count >= 2)
            {
                item.AvailableCapacityDths = capacityNumbers[1];
            }
            
            // Only return if we have essential data
            if (!string.IsNullOrEmpty(item.JobNo) && item.BeginDate.HasValue)
            {
                return item;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error parsing maintenance row: {Line}", line);
            return null;
        }
    }
}

// Supporting classes for detailed maintenance data
public class DetailedMaintenanceSchedule
{
    public string PipelineName { get; set; } = string.Empty;
    public string NoticeId { get; set; } = string.Empty;
    public string NoticeSubject { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public DateTime ExtractedAt { get; set; }
    public List<DetailedMaintenanceItem> MaintenanceItems { get; set; } = new();
    public int TotalMaintenanceJobs { get; set; }
    public decimal TotalCapacityImpactDths { get; set; }
    public DateTime? EarliestMaintenanceDate { get; set; }
    public DateTime? LatestMaintenanceDate { get; set; }
}

public class DetailedMaintenanceItem
{
    public string JobNumber { get; set; } = string.Empty;
    public string Quarter { get; set; } = string.Empty;
    public DateTime? BeginDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string GasDay { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string MaintenanceType { get; set; } = string.Empty;
    public decimal? TransportImpactDths { get; set; }
    public decimal? AvailableCapacityDths { get; set; }
    public string SourceImage { get; set; } = string.Empty;
}

public class PlannedOutageNotice
{
    public string NoticeId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
}

public class OcrTestResult
{
    public string PipelineName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string NoticeId { get; set; } = string.Empty;
    public string NoticeSubject { get; set; } = string.Empty;
    public int ImagesFound { get; set; }
    public bool OcrSuccessful { get; set; }
    public int OcrTextLength { get; set; }
    public bool ContainsMaintenanceData { get; set; }
    public int MaintenanceItemsExtracted { get; set; }
    public List<MaintenanceItem> SampleMaintenanceItems { get; set; } = new();
}

public class MaintenanceItem
{
    public string? JobNo { get; set; }
    public string? Quarter { get; set; }
    public DateTime? BeginDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? GasDay { get; set; }
    public string? Duration { get; set; }
    public string? Location { get; set; }
    public string? MaintenanceType { get; set; }
    public decimal? TransportImpactDths { get; set; }
    public decimal? AvailableCapacityDths { get; set; }
}