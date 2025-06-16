using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Notifications;

public class SlackNotificationService : INotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SlackNotificationService> _logger;
    private readonly SlackSettings _settings;

    public SlackNotificationService(HttpClient httpClient, ILogger<SlackNotificationService> logger, IOptions<SlackSettings> settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task SendNotificationAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.WebhookUrl))
        {
            _logger.LogWarning("Slack webhook URL not configured");
            return;
        }

        try
        {
            var payload = new
            {
                text = request.Subject,
                blocks = new[]
                {
                    new
                    {
                        type = "section",
                        text = new
                        {
                            type = "mrkdwn",
                            text = request.Message
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_settings.WebhookUrl, content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully sent Slack notification");
            }
            else
            {
                _logger.LogError("Failed to send Slack notification. Status: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Slack notification");
        }
    }

    public async Task SendTradingSignalAsync(TradingSignal signal, CancellationToken cancellationToken = default)
    {
        var request = new NotificationRequest
        {
            Subject = $"Trading Signal: {signal.SourceNotice.PipelineName}",
            Message = signal.FormattedMessage,
            Priority = signal.Severity switch
            {
                SignalSeverity.Critical => NotificationPriority.Urgent,
                SignalSeverity.High => NotificationPriority.High,
                _ => NotificationPriority.Normal
            }
        };

        await SendNotificationAsync(request, cancellationToken);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return !string.IsNullOrEmpty(_settings.WebhookUrl);
    }
}

public class SlackSettings
{
    public string WebhookUrl { get; set; } = string.Empty;
}

public class EmailSettings
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public List<string> DefaultRecipients { get; set; } = new();

    public bool IsConfigured => !string.IsNullOrEmpty(SmtpHost) && 
                              !string.IsNullOrEmpty(Username) && 
                              !string.IsNullOrEmpty(FromAddress);
}