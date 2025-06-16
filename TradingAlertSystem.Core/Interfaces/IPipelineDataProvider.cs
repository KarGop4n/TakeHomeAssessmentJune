using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface IPipelineDataProvider
{
    string PipelineName { get; }
    Task<List<PipelineNotice>> GetRecentNoticesAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}