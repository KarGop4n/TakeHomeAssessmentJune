using TradingAlertSystem.Core.Models;

namespace TradingAlertSystem.Core.Interfaces;

public interface INotificationService
{
    Task SendNotificationAsync(NotificationRequest request, CancellationToken cancellationToken = default);
    Task SendTradingSignalAsync(TradingSignal signal, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}