using Microsoft.Extensions.Logging;
using TradingAlertSystem.Core.Interfaces;
using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Services;

public class PipelineMonitoringService : IPipelineMonitoringService
{
    private readonly IEnumerable<IPipelineDataProvider> _pipelineProviders;
    private readonly ISignalDetectionService _signalDetectionService;
    private readonly ILogger<PipelineMonitoringService> _logger;

    public PipelineMonitoringService(
        IEnumerable<IPipelineDataProvider> pipelineProviders,
        ISignalDetectionService signalDetectionService,
        ILogger<PipelineMonitoringService> logger)
    {
        _pipelineProviders = pipelineProviders;
        _signalDetectionService = signalDetectionService;
        _logger = logger;
    }

    public async Task<List<TradingSignal>> MonitorAllPipelinesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting monitoring of {Count} pipelines", _pipelineProviders.Count());
        
        var allSignals = new List<TradingSignal>();
        var tasks = new List<Task<List<TradingSignal>>>();

        foreach (var provider in _pipelineProviders)
        {
            tasks.Add(MonitorSinglePipelineAsync(provider, cancellationToken));
        }

        var results = await Task.WhenAll(tasks);
        
        foreach (var signals in results)
        {
            allSignals.AddRange(signals);
        }

        _logger.LogInformation("Monitoring complete. Found {SignalCount} trading signals across all pipelines", allSignals.Count);
        
        return allSignals;
    }

    public async Task<List<PipelineNotice>> GetNoticesFromPipelineAsync(string pipelineName, CancellationToken cancellationToken = default)
    {
        var provider = _pipelineProviders.FirstOrDefault(p => 
            p.PipelineName.Equals(pipelineName, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            _logger.LogWarning("Pipeline provider not found for: {PipelineName}", pipelineName);
            return new List<PipelineNotice>();
        }

        try
        {
            return await provider.GetRecentNoticesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting notices from pipeline: {PipelineName}", pipelineName);
            return new List<PipelineNotice>();
        }
    }

    private async Task<List<TradingSignal>> MonitorSinglePipelineAsync(IPipelineDataProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Monitoring pipeline: {PipelineName}", provider.PipelineName);

            if (!await provider.IsAvailableAsync(cancellationToken))
            {
                _logger.LogWarning("Pipeline {PipelineName} is not available", provider.PipelineName);
                return new List<TradingSignal>();
            }

            var notices = await provider.GetRecentNoticesAsync(cancellationToken);
            
            if (!notices.Any())
            {
                _logger.LogDebug("No notices found for pipeline: {PipelineName}", provider.PipelineName);
                return new List<TradingSignal>();
            }

            var signals = await _signalDetectionService.DetectSignalsAsync(notices, cancellationToken);
            
            _logger.LogInformation("Pipeline {PipelineName}: {NoticeCount} notices processed, {SignalCount} signals detected",
                provider.PipelineName, notices.Count, signals.Count);

            return signals;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring pipeline: {PipelineName}", provider.PipelineName);
            return new List<TradingSignal>();
        }
    }
}