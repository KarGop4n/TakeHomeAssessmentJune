using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;
using TradingAlertSystem.Infrastructure.Notifications;

namespace TradingAlertSystem.Services;

public class TradingAlertService : ITradingAlertService
{
    private readonly IPipelineMonitoringService _monitoringService;
    private readonly NotificationOrchestrator _notificationOrchestrator;
    private readonly NotificationTrackingService _notificationTracker;
    private readonly EmailDigestService _emailDigestService;
    private readonly ILogger<TradingAlertService> _logger;
    private readonly MonitoringSettings _settings;

    private bool _isRunning = false;
    private int _currentCycleNumber = 0;

    public TradingAlertService(
        IPipelineMonitoringService monitoringService,
        NotificationOrchestrator notificationOrchestrator,
        NotificationTrackingService notificationTracker,
        EmailDigestService emailDigestService, 
        ILogger<TradingAlertService> logger,
        IOptions<MonitoringSettings> settings)
    {
        _monitoringService = monitoringService;
        _notificationOrchestrator = notificationOrchestrator;
        _notificationTracker = notificationTracker;
        _emailDigestService = emailDigestService;
        _logger = logger;
        _settings = settings.Value;
        MonitoringInterval = TimeSpan.FromMinutes(_settings.MonitoringIntervalMinutes);
    }

    public bool IsRunning => _isRunning;
    public int CurrentCycleNumber => _currentCycleNumber;
    public TimeSpan MonitoringInterval { get; set; }

    public async Task StartContinuousMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            _logger.LogWarning("Trading alert service is already running");
            return;
        }

        _isRunning = true;
        _currentCycleNumber = 0;

        _logger.LogInformation("Trading Alert Service Starting Continuous Monitoring");
        Console.WriteLine("Trading Alert System Started");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Monitoring Interval: {MonitoringInterval.TotalMinutes} minutes");
        Console.WriteLine($"Notification Tracking: {_settings.NotificationRetentionHours} hour retention");
        Console.WriteLine($"Pipeline Providers: 7 active");
        Console.WriteLine("Press Ctrl+C to stop gracefully");
        Console.WriteLine();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    _currentCycleNumber++;
                    await RunSingleMonitoringCycleAsync(cancellationToken);

                    if (!cancellationToken.IsCancellationRequested && _isRunning)
                    {
                        Console.WriteLine($"Next monitoring cycle in {MonitoringInterval.TotalMinutes} minutes...");
                        Console.WriteLine(new string('-', 60));

                        await Task.Delay(MonitoringInterval, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Monitoring loop cancellation requested");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in monitoring cycle {CycleNumber}", _currentCycleNumber);
                    Console.WriteLine($"Error in cycle {_currentCycleNumber}: {ex.Message}");

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    }
                }
            }
        }
        finally
        {
            _isRunning = false;
            _logger.LogInformation("Trading Alert Service Stopped");
            Console.WriteLine("\nTrading Alert System has stopped gracefully.");
        }
    }

    public async Task<List<TradingSignal>> RunSingleMonitoringCycleAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var cycleNumber = _isRunning ? _currentCycleNumber : (_currentCycleNumber + 1);

        Console.WriteLine($"Monitoring Cycle #{cycleNumber} - {startTime:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine("=====================================");

        _logger.LogInformation("Starting monitoring cycle {CycleNumber}", cycleNumber);

        _notificationTracker.CleanupOldNotifications();

        var signals = await _monitoringService.MonitorAllPipelinesAsync(cancellationToken);

        Console.WriteLine($"Monitoring Results (Cycle #{cycleNumber}):");
        Console.WriteLine($"   Total Signals Found: {signals.Count}");

        var newSignalsToReturn = new List<TradingSignal>();

        if (signals.Any())
        {
            Console.WriteLine($"   Critical: {signals.Count(s => s.Severity == SignalSeverity.Critical)}");
            Console.WriteLine($"   High: {signals.Count(s => s.Severity == SignalSeverity.High)}");
            Console.WriteLine($"   Medium: {signals.Count(s => s.Severity == SignalSeverity.Medium)}");
            Console.WriteLine($"   Low: {signals.Count(s => s.Severity == SignalSeverity.Low)}");

            // Filter out duplicate notifications
            var newSignals = _notificationTracker.FilterNewSignals(signals);
            var duplicateCount = signals.Count - newSignals.Count;

            if (duplicateCount > 0)
            {
                Console.WriteLine($"Filtered {duplicateCount} duplicate notifications");
                _logger.LogInformation("Filtered out {DuplicateCount} duplicate notifications from {TotalCount} signals",
                    duplicateCount, signals.Count);
            }

            if (newSignals.Any())
            {
                Console.WriteLine($"\nNEW Trading Signals Detected (Cycle #{cycleNumber}):");
                Console.WriteLine("-----------------------------");

                var notificationsSent = 0;

                foreach (var signal in newSignals.OrderByDescending(s => s.Severity))
                {
                    DisplaySignal(signal, cycleNumber);

                    var wasNotificationSent = await ProcessSignalNotificationAsync(signal);
                    if (wasNotificationSent)
                    {
                        notificationsSent++;
                        _notificationTracker.TrackSentNotification(signal);
                    }
                }

                Console.WriteLine($"\nNotification Summary (Cycle #{cycleNumber}):");
                Console.WriteLine($"   New signals processed: {newSignals.Count}");
                Console.WriteLine($"   Notifications sent: {notificationsSent}");
                Console.WriteLine($"   Duplicates filtered: {duplicateCount}");

                newSignalsToReturn = newSignals; //
            }
            else
            {
                Console.WriteLine("No new trading signals (all were previously sent)");
            }
        }
        else
        {
            Console.WriteLine("No trading signals detected in this cycle");
        }

        Console.WriteLine($"   Pending signals: {_emailDigestService.GetPendingSignalCount()}");

        var shouldSend = await _emailDigestService.ShouldSendDigestAsync();
        Console.WriteLine($"   Should send digest: {shouldSend}");

        if (shouldSend)
        {
            Console.WriteLine($"Sending email digest with {_emailDigestService.GetPendingSignalCount()} signals...");
            try
            {
                await _emailDigestService.SendDigestEmailAsync(cancellationToken);
                Console.WriteLine($"Email digest sent successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Email digest failed: {ex.Message}");
                _logger.LogError(ex, "Failed to send email digest");
            }
        }
        else
        {
            Console.WriteLine($"⏳ Email digest not ready to send yet");
        }

        var completionDuration = DateTime.UtcNow - startTime;
        Console.WriteLine($"\nCycle #{cycleNumber} completed in {completionDuration.TotalSeconds:F1} seconds");
        Console.WriteLine($"Tracking {_notificationTracker.GetTrackedNotificationCount()} sent notifications");

        _logger.LogInformation("Completed monitoring cycle {CycleNumber} in {Duration:F1} seconds with {NewSignals} new signals",
            cycleNumber, completionDuration.TotalSeconds, newSignalsToReturn.Count);

        return newSignalsToReturn;
    }

    private async Task<bool> ProcessSignalNotificationAsync(TradingSignal signal)
    {
        try
        {
            await _notificationOrchestrator.ProcessSignalAsync(signal);

            _emailDigestService.AddSignalToDigest(signal);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending notification for signal {SignalId}", signal.Id);
            Console.WriteLine($"Notification failed: {ex.Message}");
            return false;
        }
    }

    private void DisplaySignal(TradingSignal signal, int cycleNumber)
    {
        var severityIcon = signal.Severity switch
        {
            SignalSeverity.Critical => "🔴",
            SignalSeverity.High => "🟠",
            SignalSeverity.Medium => "🟡",
            _ => "🟢"
        };

        Console.WriteLine($"{severityIcon} {signal.Severity} - {signal.SourceNotice.PipelineName}");
        Console.WriteLine($"   Title: {signal.SourceNotice.Title}");
        Console.WriteLine($"   Type: {signal.SourceNotice.NoticeType}");
        Console.WriteLine($"   Date: {signal.SourceNotice.NoticeDate:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"   Location: {signal.SourceNotice.Location}");

        if (signal.SourceNotice.VolumeImpactMmbtu.HasValue)
        {
            Console.WriteLine($"   Volume: {signal.SourceNotice.VolumeImpactMmbtu:N0} {signal.SourceNotice.VolumeUnit}");
        }

        Console.WriteLine($"   Rules: {string.Join(", ", signal.TriggeredRules)}");
        Console.WriteLine($"   NEW in Cycle #{cycleNumber}");
        Console.WriteLine();
    }
}