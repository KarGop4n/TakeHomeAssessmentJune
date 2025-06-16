namespace TradingAlertSystem.Core.Models;

public class TradingSignal
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public PipelineNotice SourceNotice { get; set; } = null!;
    public SignalSeverity Severity { get; set; }
    public List<string> TriggeredRules { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public bool RequiresImmediateAttention { get; set; }
    
    public string FormattedMessage => 
        $"{Severity} Trading Signal Detected\n" +
        $"Pipeline: {SourceNotice.PipelineName}\n" +
        $"Type: {SourceNotice.NoticeType}\n" +
        $"Title: {SourceNotice.Title}\n" +
        $"Location: {SourceNotice.Location}\n" +
        $"Date: {SourceNotice.NoticeDate:yyyy-MM-dd HH:mm}\n" +
        $"Volume Impact: {(SourceNotice.VolumeImpactMmbtu?.ToString("N0") ?? "Unknown")} {SourceNotice.VolumeUnit ?? ""}\n" +
        $"Source: {SourceNotice.SourceUrl}\n" +
        $"Triggered Rules: {string.Join(", ", TriggeredRules)}";
}

public enum SignalSeverity
{
    Low,
    Medium,
    High,
    Critical
}