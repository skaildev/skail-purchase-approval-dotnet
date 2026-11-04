using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Worker.Progress;

public interface IProgressNotifier
{
    Task NotifyAsync(OrderEvent evt, CancellationToken ct = default);
}
