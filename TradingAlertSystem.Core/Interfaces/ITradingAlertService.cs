using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface ITradingAlertService
{
    Task StartContinuousMonitoringAsync(CancellationToken cancellationToken = default);
    Task<List<TradingSignal>> RunSingleMonitoringCycleAsync(CancellationToken cancellationToken = default);
    bool IsRunning { get; }
    int CurrentCycleNumber { get; }
    TimeSpan MonitoringInterval { get; set; }
}