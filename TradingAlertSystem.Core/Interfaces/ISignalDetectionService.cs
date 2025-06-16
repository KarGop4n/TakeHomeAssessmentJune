using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface ISignalDetectionService
{
    Task<List<TradingSignal>> DetectSignalsAsync(List<PipelineNotice> notices, CancellationToken cancellationToken = default);
    void AddDetectionRule(string ruleName, Func<PipelineNotice, bool> rule, SignalSeverity severity);
}