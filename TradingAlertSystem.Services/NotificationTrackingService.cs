using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Services;

public class NotificationTrackingService
{
    private readonly ILogger<NotificationTrackingService> _logger;
    private readonly Dictionary<string, DateTime> _sentNotifications = new();
    private readonly TimeSpan _retentionPeriod;
    private readonly object _lock = new object();

    public NotificationTrackingService(ILogger<NotificationTrackingService> logger, IOptions<MonitoringSettings> settings)
    {
        _logger = logger;
        _retentionPeriod = TimeSpan.FromHours(settings.Value.NotificationRetentionHours);
    }

    public List<TradingSignal> FilterNewSignals(List<TradingSignal> signals)
    {
        lock (_lock)
        {
            var newSignals = new List<TradingSignal>();

            foreach (var signal in signals)
            {
                var notificationKey = GenerateNotificationKey(signal);
                
                if (!_sentNotifications.ContainsKey(notificationKey))
                {
                    newSignals.Add(signal);
                }
                else
                {
                    _logger.LogDebug("Filtered duplicate notification: {Key}", notificationKey);
                }
            }

            return newSignals;
        }
    }

    public void TrackSentNotification(TradingSignal signal)
    {
        lock (_lock)
        {
            var notificationKey = GenerateNotificationKey(signal);
            _sentNotifications[notificationKey] = DateTime.UtcNow;
            
            _logger.LogDebug("Tracking sent notification: {Key}", notificationKey);
        }
    }

    public void CleanupOldNotifications()
    {
        lock (_lock)
        {
            var cutoffTime = DateTime.UtcNow - _retentionPeriod;
            var oldKeys = _sentNotifications
                .Where(kv => kv.Value < cutoffTime)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldKeys)
            {
                _sentNotifications.Remove(key);
            }

            if (oldKeys.Any())
            {
                _logger.LogDebug("Cleaned up {Count} old notification tracking entries", oldKeys.Count);
            }
        }
    }

    public int GetTrackedNotificationCount()
    {
        lock (_lock)
        {
            return _sentNotifications.Count;
        }
    }

    private string GenerateNotificationKey(TradingSignal signal)
    {
        // Create unique key based on notice content and detection time
        var notice = signal.SourceNotice;
        return $"{notice.PipelineName}|{notice.Id}|{notice.NoticeType}|{notice.NoticeDate:yyyyMMddHHmm}|{signal.Severity}";
    }
}