using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Models;
using TradingAlertSystem.Infrastructure.Notifications;

namespace TradingAlertSystem.Services;

public class NotificationOrchestrator
{
    private readonly SlackNotificationService _slackService;
    private readonly ILogger<NotificationOrchestrator> _logger;

    public NotificationOrchestrator(
        SlackNotificationService slackService,
        ILogger<NotificationOrchestrator> logger)
    {
        _slackService = slackService;
        _logger = logger;
    }

    public async Task ProcessSignalAsync(TradingSignal signal, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing trading signal for {Pipeline} with severity {Severity}", 
            signal.SourceNotice.PipelineName, signal.Severity);

        try
        {
            // Send Slack notification
            if (await _slackService.IsAvailableAsync(cancellationToken))
            {
                await _slackService.SendTradingSignalAsync(signal, cancellationToken);
                _logger.LogInformation("Successfully sent Slack notification for signal {SignalId}", signal.Id);
            }
            else
            {
                _logger.LogWarning("Slack service is not available for signal {SignalId}", signal.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Slack notification for signal {SignalId}", signal.Id);
            throw;
        }
    }
}