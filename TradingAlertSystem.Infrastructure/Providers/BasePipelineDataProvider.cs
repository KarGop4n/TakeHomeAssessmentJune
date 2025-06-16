using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Infrastructure.Providers;

public abstract class BasePipelineDataProvider : IPipelineDataProvider
{
    protected readonly HttpClient _httpClient;
    protected readonly ILogger _logger;
    protected readonly PipelineConfiguration _config;

    protected BasePipelineDataProvider(HttpClient httpClient, ILogger logger, PipelineConfiguration config)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;
    }

    public abstract string PipelineName { get; }

    public virtual async Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching notices from {Pipeline}", PipelineName);
            
            var htmlContent = await FetchHtmlContentAsync(cancellationToken);
            if (string.IsNullOrEmpty(htmlContent))
            {
                _logger.LogWarning("No content received from {Pipeline}", PipelineName);
                return new List<PipelineNotice>();
            }

            var notices = await ParseNoticesFromHtmlAsync(htmlContent, cancellationToken);
            
            _logger.LogInformation("Successfully fetched {Count} notices from {Pipeline}", notices.Count, PipelineName);
            return notices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching notices from {Pipeline}", PipelineName);
            return new List<PipelineNotice>();
        }
    }

    public virtual async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(_config.BaseUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected virtual async Task<string> FetchHtmlContentAsync(CancellationToken cancellationToken)
    {
        var url = $"{_config.BaseUrl.TrimEnd('/')}/{_config.NoticesPath.TrimStart('/')}";
        
        await Task.Delay(_config.RequestDelayMs, cancellationToken);
        
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    protected virtual async Task<List<PipelineNotice>> ParseNoticesFromHtmlAsync(string htmlContent, CancellationToken cancellationToken)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var notices = new List<PipelineNotice>();
        var noticeNodes = doc.DocumentNode.SelectNodes(_config.ParsingRules.NoticeRowSelector);

        if (noticeNodes == null)
        {
            _logger.LogWarning("No notice nodes found using selector: {Selector}", _config.ParsingRules.NoticeRowSelector);
            return notices;
        }

        foreach (var node in noticeNodes)
        {
            try
            {
                var notice = await ParseSingleNoticeAsync(node, cancellationToken);
                if (notice != null)
                {
                    notices.Add(notice);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing individual notice from {Pipeline}", PipelineName);
            }
        }

        return notices;
    }

    protected virtual async Task<PipelineNotice?> ParseSingleNoticeAsync(HtmlNode node, CancellationToken cancellationToken)
    {
        var notice = new PipelineNotice
        {
            PipelineName = PipelineName,
            Id = Guid.NewGuid().ToString()
        };

        notice.Title = ExtractTextFromSelector(node, _config.ParsingRules.TitleSelector);
        notice.NoticeType = ExtractTextFromSelector(node, _config.ParsingRules.TypeSelector);
        notice.Location = ExtractTextFromSelector(node, _config.ParsingRules.LocationSelector);
        
        var dateText = ExtractTextFromSelector(node, _config.ParsingRules.DateSelector);
        if (!string.IsNullOrEmpty(dateText) && DateTime.TryParse(dateText, out var parsedDate))
        {
            notice.NoticeDate = parsedDate;
        }

        var linkNode = node.SelectSingleNode(_config.ParsingRules.LinkSelector);
        if (linkNode != null)
        {
            var href = linkNode.GetAttributeValue("href", "");
            if (!string.IsNullOrEmpty(href))
            {
                notice.SourceUrl = href.StartsWith("http") ? href : $"{_config.BaseUrl.TrimEnd('/')}/{href.TrimStart('/')}";
            }
        }

        if (!string.IsNullOrEmpty(_config.ParsingRules.DescriptionSelector))
        {
            notice.Description = ExtractTextFromSelector(node, _config.ParsingRules.DescriptionSelector);
        }

        ExtractVolumeInformation(notice);

        return await Task.FromResult(notice);
    }

    protected virtual string ExtractTextFromSelector(HtmlNode parentNode, string selector)
    {
        var node = parentNode.SelectSingleNode(selector);
        return node?.InnerText?.Trim() ?? string.Empty;
    }

    protected virtual void ExtractVolumeInformation(PipelineNotice notice)
    {
        var searchText = $"{notice.Title} {notice.Description}";
        
        foreach (var pattern in _config.ParsingRules.VolumeRegexPatterns)
        {
            var match = Regex.Match(searchText, pattern, RegexOptions.IgnoreCase);
            if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(",", ""), out var volume))
            {
                notice.VolumeImpactMmbtu = volume;
                notice.VolumeUnit = match.Groups[2].Value;
                break;
            }
        }
    }
}