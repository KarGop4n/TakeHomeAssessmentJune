// Enhanced SignalDetectionService.cs with date filtering

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Services;

public class SignalDetectionService : ISignalDetectionService
{
    private readonly ILogger<SignalDetectionService> _logger;
    private readonly DetectionSettings _settings;
    private readonly Dictionary<string, (Func<PipelineNotice, bool> Rule, SignalSeverity Severity)> _detectionRules;

    public SignalDetectionService(ILogger<SignalDetectionService> logger, IOptions<DetectionSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
        _detectionRules = new Dictionary<string, (Func<PipelineNotice, bool>, SignalSeverity)>();
        
        InitializeDefaultRules();
    }

    public async Task<List<TradingSignal>> DetectSignalsAsync(List<PipelineNotice> notices, CancellationToken cancellationToken = default)
    {
        var signals = new List<TradingSignal>();

        var recentNotices = FilterByTradingRelevantDate(notices);
        
        if (recentNotices.Count < notices.Count)
        {
            var filteredOut = notices.Count - recentNotices.Count;
            _logger.LogInformation("Filtered out {FilteredCount} notices older than {MaxAge} days (keeping {RecentCount})", 
                filteredOut, _settings.MaxNoticeAgeForTradingDays, recentNotices.Count);
        }

        foreach (var notice in recentNotices)
        {
            var triggeredRules = new List<string>();
            var maxSeverity = SignalSeverity.Low;

            foreach (var (ruleName, (rule, severity)) in _detectionRules)
            {
                try
                {
                    if (rule(notice))
                    {
                        triggeredRules.Add(ruleName);
                        if (severity > maxSeverity)
                        {
                            maxSeverity = severity;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error evaluating rule {RuleName} for notice {NoticeId}", ruleName, notice.Id);
                }
            }

            if (triggeredRules.Any())
            {
                var signal = new TradingSignal
                {
                    SourceNotice = notice,
                    Severity = maxSeverity,
                    TriggeredRules = triggeredRules,
                    Summary = GenerateSignalSummary(notice, triggeredRules, maxSeverity),
                    RequiresImmediateAttention = maxSeverity >= SignalSeverity.High
                };

                signals.Add(signal);
                _logger.LogInformation("Trading signal detected: {Pipeline} - {Severity} - {Rules} - Age: {Age} days", 
                    notice.PipelineName, maxSeverity, string.Join(", ", triggeredRules),
                    (DateTime.UtcNow - notice.NoticeDate).Days);
            }
        }

        return await Task.FromResult(signals);
    }

    private List<PipelineNotice> FilterByTradingRelevantDate(List<PipelineNotice> notices)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-_settings.MaxNoticeAgeForTradingDays);
        
        var recentNotices = notices.Where(notice => 
        {
            if (notice.NoticeDate.Date == DateTime.UtcNow.Date)
                return true;
                
            return notice.NoticeDate >= cutoffDate;
        }).ToList();

        if (notices.Any())
        {
            var ageGroups = notices.GroupBy(n => 
            {
                var age = (DateTime.UtcNow - n.NoticeDate).Days;
                return age switch
                {
                    0 => "Today",
                    1 => "Yesterday", 
                    <= 7 => "This Week",
                    <= 30 => "This Month",
                    <= 90 => "Last 3 Months",
                    _ => "Older"
                };
            }).ToDictionary(g => g.Key, g => g.Count());

            _logger.LogDebug("Notice age distribution: {AgeDistribution}", 
                string.Join(", ", ageGroups.Select(kv => $"{kv.Key}: {kv.Value}")));
        }

        return recentNotices;
    }

    public void AddDetectionRule(string ruleName, Func<PipelineNotice, bool> rule, SignalSeverity severity)
    {
        _detectionRules[ruleName] = (rule, severity);
        _logger.LogInformation("Added detection rule: {RuleName} with severity {Severity}", ruleName, severity);
    }

    private void InitializeDefaultRules()
    {
        AddDetectionRule("Force Majeure Keywords", 
            notice => notice.ContainsKeyword("force majeure", "emergency", "unplanned"), 
            SignalSeverity.Critical);

        AddDetectionRule("Curtailment Keywords",
            notice => notice.ContainsKeyword("curtailment", "outage", "shutdown", "restriction", "capacity reduction"),
            SignalSeverity.High);

        AddDetectionRule("Louisiana/Henry Hub Location",
            notice => notice.ContainsKeyword("louisiana", "henry hub", "la", "gulf coast") ||
                     notice.Location.Contains("louisiana", StringComparison.OrdinalIgnoreCase) ||
                     notice.Location.Contains("henry hub", StringComparison.OrdinalIgnoreCase),
            SignalSeverity.High);

        AddDetectionRule("Very Recent Notice",
            notice => notice.IsWithinDays(1), 
            SignalSeverity.Medium);

        AddDetectionRule("Recent Notice",
            notice => notice.IsWithinDays(_settings.RecentNoticeDays) && !notice.IsWithinDays(1),
            SignalSeverity.Low);

        AddDetectionRule("Critical Notice Type",
            notice => notice.NoticeType.Contains("critical", StringComparison.OrdinalIgnoreCase) ||
                     notice.NoticeType.Contains("urgent", StringComparison.OrdinalIgnoreCase),
            SignalSeverity.High);

        AddDetectionRule("High Volume Impact",
            notice => notice.VolumeImpactMmbtu.HasValue && 
                     notice.VolumeImpactMmbtu.Value >= _settings.HighVolumeThresholdMmbtu,
            SignalSeverity.High);

        AddDetectionRule("Medium Volume Impact",
            notice => notice.VolumeImpactMmbtu.HasValue && 
                     notice.VolumeImpactMmbtu.Value >= _settings.MediumVolumeThresholdMmbtu,
            SignalSeverity.Medium);

        AddDetectionRule("LNG Terminal Impact",
            notice => notice.ContainsKeyword("lng", "export", "terminal", "sabine", "cameron", "freeport"),
            SignalSeverity.High);

        AddDetectionRule("Recent Planned Outage",
            notice => (notice.NoticeType.Contains("planned", StringComparison.OrdinalIgnoreCase) ||
                      notice.NoticeType.Contains("outage", StringComparison.OrdinalIgnoreCase)) &&
                      notice.IsWithinDays(30), // Planned outages relevant for next 30 days
            SignalSeverity.High);

        _logger.LogInformation("Initialized {Count} default detection rules with date filtering", _detectionRules.Count);
    }

    private string GenerateSignalSummary(PipelineNotice notice, List<string> triggeredRules, SignalSeverity severity)
    {
        var keyEvents = new List<string>();

        if (notice.ContainsKeyword("force majeure"))
            keyEvents.Add("Force Majeure declared");
        
        if (notice.ContainsKeyword("curtailment", "outage"))
            keyEvents.Add("Service disruption");
        
        if (notice.VolumeImpactMmbtu.HasValue)
            keyEvents.Add($"Volume impact: {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");

        if (notice.ContainsKeyword("louisiana", "henry hub"))
            keyEvents.Add("Henry Hub area affected");

        var age = (DateTime.UtcNow - notice.NoticeDate).Days;
        if (age == 0)
            keyEvents.Add("Posted today");
        else if (age == 1)
            keyEvents.Add("Posted yesterday");
        else if (age <= 7)
            keyEvents.Add($"Posted {age} days ago");

        var summary = keyEvents.Any() 
            ? string.Join(", ", keyEvents)
            : $"{severity} trading signal on {notice.PipelineName}";

        return summary;
    }
}

public class DetectionSettings
{
    public int RecentNoticeDays { get; set; } = 3;
    public int MaxNoticeAgeForTradingDays { get; set; } = 30; // Maximum age for trading relevance
    public decimal HighVolumeThresholdMmbtu { get; set; } = 1000;
    public decimal MediumVolumeThresholdMmbtu { get; set; } = 500;
    public List<string> CriticalKeywords { get; set; } = new() 
    { 
        "force majeure", "emergency", "unplanned", "critical" 
    };
    public List<string> HenryHubKeywords { get; set; } = new() 
    { 
        "louisiana", "henry hub", "gulf coast", "la" 
    };
}