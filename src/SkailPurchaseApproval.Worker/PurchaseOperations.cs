using Microsoft.Extensions.Logging;
using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Worker.Progress;

namespace SkailPurchaseApproval.Worker;

/// <summary>
/// The I/O behind the commands, as plain tasks. Commands only run inside the skail runtime, so keeping the
/// logic here lets it be unit tested without skail. The web app plays the manager's and the director's inboxes:
/// delivering a request is the event it receives, and if it cannot be delivered the command throws and skail retries.
/// </summary>
public class PurchaseOperations(IProgressNotifier notifier, ILogger<PurchaseOperations> log)
{
    /// <summary>
    /// Idempotent: the web app records one request per level. Asking the director is the escalation,
    /// reported as its own stage so the page can mark the manager as "no response".
    /// </summary>
    public async Task RequestApprovalAsync(PurchaseOrder purchase, string level)
    {
        if (!Levels.IsValid(level))
            throw new ArgumentException($"unknown level '{level}'", nameof(level));

        log.LogInformation("Approval requested from the {Level}: order {OrderId} ({Supplier}, {Amount})", level, purchase.Id, purchase.Supplier, purchase.Amount);
        await notifier.NotifyAsync(level == Levels.Manager
            ? OrderEvent.ApprovalRequested(purchase.Id, level)
            : OrderEvent.Escalated(purchase.Id, level));
    }

    /// <summary>Idempotent: the result derives from the arguments, and the web app ignores a repeated stage.</summary>
    public async Task<OrderResult> ConfirmAsync(PurchaseOrder purchase, Decision decision)
    {
        if (!decision.Approved)
            throw new InvalidOperationException("only an approved decision confirms the purchase");

        var result = OrderResult.Approved(purchase.Id, decision);
        log.LogInformation("Order {OrderId} approved by the {Level} ({Reference})", purchase.Id, decision.Level, result.Reference);
        await notifier.NotifyAsync(OrderEvent.Approved(purchase.Id, result));
        return result;
    }

    /// <summary>An explicit rejection: a business result, not an exception. Idempotent like <see cref="ConfirmAsync"/>.</summary>
    public async Task<OrderResult> RejectAsync(PurchaseOrder purchase, Decision decision)
    {
        if (decision.Approved)
            throw new InvalidOperationException("only a rejected decision rejects the purchase");

        var result = OrderResult.Rejected(purchase.Id, decision);
        log.LogInformation("Order {OrderId} rejected by the {Level} ({Reason})", purchase.Id, decision.Level, decision.Reason);
        await notifier.NotifyAsync(OrderEvent.Rejected(purchase.Id, result));
        return result;
    }

    /// <summary>Both deadlines passed without an answer. Idempotent like <see cref="ConfirmAsync"/>.</summary>
    public async Task<OrderResult> CancelForNoResponseAsync(PurchaseOrder purchase)
    {
        var result = OrderResult.NoResponse(purchase.Id);
        log.LogInformation("Order {OrderId} cancelled: no response from the manager or the director", purchase.Id);
        await notifier.NotifyAsync(OrderEvent.Cancelled(purchase.Id, result));
        return result;
    }

    public Task ReportStatusAsync(OrderEvent evt) => notifier.NotifyAsync(evt);
}
