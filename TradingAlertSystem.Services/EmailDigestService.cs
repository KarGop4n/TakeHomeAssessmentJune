using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Mail;
using System.Text;
using TradingAlertSystem.Core.Models;
using TradingAlertSystem.Infrastructure.Notifications;

namespace TradingAlertSystem.Services;

public class EmailDigestService
{
    private readonly ILogger<EmailDigestService> _logger;
    private readonly EmailSettings _emailSettings;
    private readonly NotificationSettings _notificationSettings;
    private readonly List<TradingSignal> _recentSignals = new();
    private readonly object _lock = new object();
    private DateTime _lastEmailSent = DateTime.MinValue;

    public EmailDigestService(
        ILogger<EmailDigestService> logger, 
        IOptions<EmailSettings> emailSettings,
        IOptions<NotificationSettings> notificationSettings)
    {
        _logger = logger;
        _emailSettings = emailSettings.Value;
        _notificationSettings = notificationSettings.Value;
    }

    public void AddSignalToDigest(TradingSignal signal)
    {
        lock (_lock)
        {
            _recentSignals.RemoveAll(s => s.SourceNotice.Id == signal.SourceNotice.Id);
            
            _recentSignals.Add(signal);
            
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _recentSignals.RemoveAll(s => s.DetectedAt < cutoff);
            
            _logger.LogDebug("Added signal to email digest. Total signals: {Count}", _recentSignals.Count);
        }
    }

    public async Task<bool> ShouldSendDigestAsync()
    {
        var timeSinceLastEmail = DateTime.UtcNow - _lastEmailSent;
        var shouldSend = timeSinceLastEmail.TotalHours >= _notificationSettings.EmailFrequencyHours;
        
        lock (_lock)
        {
            var hasCriticalSignals = _recentSignals.Any(s => s.Severity == SignalSeverity.Critical);
            if (hasCriticalSignals && timeSinceLastEmail.TotalHours >= 1)
            {
                shouldSend = true;
            }
        }

        return shouldSend;
    }

    public async Task SendDigestEmailAsync(CancellationToken cancellationToken = default)
    {
        if (!_emailSettings.IsConfigured)
        {
            _logger.LogWarning("Email settings not configured, skipping digest email");
            return;
        }

        List<TradingSignal> signalsToSend;
        lock (_lock)
        {
            if (!_recentSignals.Any())
            {
                _logger.LogInformation("No recent signals for email digest");
                return;
            }

            signalsToSend = _recentSignals.ToList();
        }

        try
        {
            var markdown = GenerateMarkdownDigest(signalsToSend);
            var subject = GenerateEmailSubject(signalsToSend);

            using var smtpClient = new SmtpClient(_emailSettings.SmtpHost, _emailSettings.SmtpPort)
            {
                EnableSsl = _emailSettings.EnableSsl,
                Credentials = new System.Net.NetworkCredential(_emailSettings.Username, _emailSettings.Password)
            };

            foreach (var recipient in _emailSettings.DefaultRecipients)
            {
                var mailMessage = new MailMessage(_emailSettings.FromAddress, recipient)
                {
                    Subject = subject,
                    Body = markdown,
                    IsBodyHtml = false
                };

                var attachmentContent = Encoding.UTF8.GetBytes(markdown);
                var attachmentStream = new MemoryStream(attachmentContent);
                var attachment = new Attachment(attachmentStream, $"TradingSignals_{DateTime.UtcNow:yyyyMMdd_HHmm}.md", "text/markdown");
                mailMessage.Attachments.Add(attachment);

                await smtpClient.SendMailAsync(mailMessage, cancellationToken);
            }

            _lastEmailSent = DateTime.UtcNow;
            _logger.LogInformation("Successfully sent email digest to {RecipientCount} recipients with {SignalCount} signals", 
                _emailSettings.DefaultRecipients.Count, signalsToSend.Count);

            lock (_lock)
            {
                var cutoff = DateTime.UtcNow.AddHours(-_notificationSettings.EmailFrequencyHours);
                _recentSignals.RemoveAll(s => s.DetectedAt < cutoff);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email digest");
        }
    }

    private string GenerateEmailSubject(List<TradingSignal> signals)
    {
        var criticalCount = signals.Count(s => s.Severity == SignalSeverity.Critical);
        var highCount = signals.Count(s => s.Severity == SignalSeverity.High);
        var totalCount = signals.Count;

        if (criticalCount > 0)
        {
            return $"🔴 CRITICAL: {criticalCount} Critical Trading Signals ({totalCount} total)";
        }
        else if (highCount > 0)
        {
            return $"🟠 HIGH: {highCount} High Priority Trading Signals ({totalCount} total)";
        }
        else
        {
            return $"Trading Signal Digest: {totalCount} signals detected";
        }
    }

    private string GenerateMarkdownDigest(List<TradingSignal> signals)
    {
        var md = new StringBuilder();
        var now = DateTime.UtcNow;
        
        md.AppendLine("# Trading Signal Digest Report");
        md.AppendLine($"**Generated:** {now:yyyy-MM-dd HH:mm:ss} UTC");
        md.AppendLine($"**Period:** Last {_notificationSettings.EmailFrequencyHours} hours");
        md.AppendLine();

        md.AppendLine("## Summary");
        md.AppendLine($"- **Total Signals:** {signals.Count}");
        md.AppendLine($"- **Critical:** {signals.Count(s => s.Severity == SignalSeverity.Critical)}");
        md.AppendLine($"- **High:** {signals.Count(s => s.Severity == SignalSeverity.High)}");
        md.AppendLine($"- **Medium:** {signals.Count(s => s.Severity == SignalSeverity.Medium)}");
        md.AppendLine($"- **Low:** {signals.Count(s => s.Severity == SignalSeverity.Low)}");
        md.AppendLine();

        var pipelineGroups = signals.GroupBy(s => s.SourceNotice.PipelineName)
            .OrderByDescending(g => g.Count());
        
        md.AppendLine("## Pipeline Breakdown");
        foreach (var group in pipelineGroups)
        {
            md.AppendLine($"- **{group.Key}:** {group.Count()} signals");
        }
        md.AppendLine();

        var criticalSignals = signals.Where(s => s.Severity == SignalSeverity.Critical).OrderByDescending(s => s.DetectedAt);
        if (criticalSignals.Any())
        {
            md.AppendLine("## 🔴 Critical Alerts");
            foreach (var signal in criticalSignals)
            {
                AppendSignalToMarkdown(md, signal);
            }
            md.AppendLine();
        }

        var highSignals = signals.Where(s => s.Severity == SignalSeverity.High).OrderByDescending(s => s.DetectedAt);
        if (highSignals.Any())
        {
            md.AppendLine("## 🟠 High Priority Alerts");
            foreach (var signal in highSignals)
            {
                AppendSignalToMarkdown(md, signal);
            }
            md.AppendLine();
        }

        var otherSignals = signals.Where(s => s.Severity <= SignalSeverity.Medium).OrderByDescending(s => s.DetectedAt);
        if (otherSignals.Any())
        {
            md.AppendLine("## 📊 Other Signals");
            foreach (var signal in otherSignals)
            {
                AppendSignalToMarkdown(md, signal);
            }
        }

        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine("*This report was automatically generated by the Trading Alert System*");

        return md.ToString();
    }

    private void AppendSignalToMarkdown(StringBuilder md, TradingSignal signal)
    {
        var notice = signal.SourceNotice;
        var severityIcon = signal.Severity switch
        {
            SignalSeverity.Critical => "🔴",
            SignalSeverity.High => "🟠", 
            SignalSeverity.Medium => "🟡",
            _ => "🟢"
        };

        md.AppendLine($"### {severityIcon} {notice.PipelineName} - {signal.Severity}");
        md.AppendLine($"**Title:** {notice.Title}");
        md.AppendLine($"**Type:** {notice.NoticeType}");
        md.AppendLine($"**Date:** {notice.NoticeDate:yyyy-MM-dd HH:mm}");
        md.AppendLine($"**Location:** {notice.Location}");
        
        if (notice.VolumeImpactMmbtu.HasValue)
        {
            md.AppendLine($"**Volume Impact:** {notice.VolumeImpactMmbtu:N0} {notice.VolumeUnit}");
        }
        
        md.AppendLine($"**Triggered Rules:** {string.Join(", ", signal.TriggeredRules)}");
        md.AppendLine($"**Source:** [View Notice]({notice.SourceUrl})");
        md.AppendLine($"**Detected:** {signal.DetectedAt:yyyy-MM-dd HH:mm:ss} UTC");
        md.AppendLine();
    }

    public int GetPendingSignalCount()
    {
        lock (_lock)
        {
            return _recentSignals.Count;
        }
    }
}
