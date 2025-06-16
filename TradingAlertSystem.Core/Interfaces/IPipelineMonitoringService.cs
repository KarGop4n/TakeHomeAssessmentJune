using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface IPipelineMonitoringService
{
    Task<List<TradingSignal>> MonitorAllPipelinesAsync(CancellationToken cancellationToken = default);
    Task<List<PipelineNotice>> GetNoticesFromPipelineAsync(string pipelineName, CancellationToken cancellationToken = default);
}