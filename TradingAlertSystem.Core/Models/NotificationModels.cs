namespace TradingAlertSystem.Core.Models;

public class NotificationRequest
{
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public List<string> Recipients { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class PipelineConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string NoticesPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int RequestDelayMs { get; set; } = 1000;
    public ParsingRules ParsingRules { get; set; } = new();
}

public class ParsingRules
{
    public string NoticeRowSelector { get; set; } = string.Empty;
    public string TitleSelector { get; set; } = string.Empty;
    public string TypeSelector { get; set; } = string.Empty;
    public string DateSelector { get; set; } = string.Empty;
    public string LocationSelector { get; set; } = string.Empty;
    public string LinkSelector { get; set; } = string.Empty;
    public string? DescriptionSelector { get; set; }
    public string DateFormat { get; set; } = string.Empty;
    public List<string> VolumeRegexPatterns { get; set; } = new();
}

public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Urgent
}