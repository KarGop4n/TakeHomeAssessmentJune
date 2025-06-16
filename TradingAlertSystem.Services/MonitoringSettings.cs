namespace TradingAlertSystem.Services;

public class MonitoringSettings
{
    public int MonitoringIntervalMinutes { get; set; } = 5;
    public int NotificationRetentionHours { get; set; } = 24;
    public bool EnableConsoleOutput { get; set; } = true;
    public bool EnableDetailedLogging { get; set; } = true;
}