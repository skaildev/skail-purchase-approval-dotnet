using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Worker;

/// <summary>
/// The I/O of the function, one operation per command. A retry runs the whole command again, so every command is
/// idempotent. The commands delegate to <see cref="PurchaseOperations"/>, so their logic is tested without skail.
/// </summary>
public class PurchaseCommands(PurchaseOperations operations)
{
    [SkailCommand(retryCount: 3)]
    public async SkailTask RequestApproval(PurchaseOrder purchase, string level)
        => await operations.RequestApprovalAsync(purchase, level);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<OrderResult> ConfirmPurchase(PurchaseOrder purchase, Decision decision)
        => await operations.ConfirmAsync(purchase, decision);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<OrderResult> RejectPurchase(PurchaseOrder purchase, Decision decision)
        => await operations.RejectAsync(purchase, decision);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<OrderResult> CancelForNoResponse(PurchaseOrder purchase)
        => await operations.CancelForNoResponseAsync(purchase);

    /// <summary>Idempotent: the web app ignores a repeated event (same order and stage).</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask ReportStatus(OrderEvent evt)
        => await operations.ReportStatusAsync(evt);
}
