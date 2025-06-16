using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface INoticeParser
{
    Task<List<PipelineNotice>> ParseNoticesAsync(string htmlContent, string baseUrl, string pipelineName, CancellationToken cancellationToken = default);
}