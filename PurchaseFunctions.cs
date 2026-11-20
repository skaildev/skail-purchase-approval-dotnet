using System.Globalization;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailPurchaseApproval.Demo;

namespace SkailPurchaseApproval;

public class PurchaseFunctions(Timeline timeline)
{
    /// <summary>
    /// Asks the manager to approve the order and waits for the decision until a deadline. With no answer it escalates
    /// to the director and waits again; with no answer again, it cancels the order. The deadlines are arguments, so the
    /// same code serves the demo (1 minute) and a real approval (48 hours).
    /// The name and the parameters are the trigger contract: [{purchase}, {managerDeadlineSeconds}, {directorDeadlineSeconds}].
    /// </summary>
    [SkailFunction]
    public async SkailTask<string> ApprovePurchase(PurchaseOrder purchase, int managerDeadlineSeconds, int directorDeadlineSeconds)
    {
        await RequestApproval(purchase, Levels.Manager);

        // the wait is created once, without await, and reused in both WhenAny
        var decision = SkailTask.WaitForEvent<Decision>(Events.PurchaseDecision, purchase.Id);

        // decide by the task WhenAny returns, not by decision.IsCompleted: on a resume, IsCompleted already sees a
        // decision that arrived after the deadline
        var winner = await SkailTask.WhenAny(decision, SkailTask.DelaySeconds((ulong)managerDeadlineSeconds));
        if (winner != decision)
        {
            await RequestApproval(purchase, Levels.Director);   // escalation
            winner = await SkailTask.WhenAny(decision, SkailTask.DelaySeconds((ulong)directorDeadlineSeconds));
        }

        if (winner != decision)
            return await CancelForNoResponse(purchase);

        var answer = await decision;
        return answer.Approved
            ? await ConfirmPurchase(purchase, answer)
            : await RejectPurchase(purchase, answer);
    }

    // The commands are the I/O: here they only report what would happen in the purchasing system, to the console and
    // to the page.
    // A completed command never runs again, so a replay does not ask the approvers again nor print the same line twice.

    [SkailCommand(retryCount: 3)]
    public async SkailTask RequestApproval(PurchaseOrder purchase, string level) =>
        await Report(purchase.Id, $"{Levels.Label(level)}: approval requested for {purchase.Description}, " +
            $"{purchase.Supplier}, R$ {purchase.Amount.ToString("N2", CultureInfo.InvariantCulture)}", $"requested:{level}");

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> ConfirmPurchase(PurchaseOrder purchase, Decision decision)
    {
        var reference = "PO-" + purchase.Id[..8].ToUpperInvariant();
        await Report(purchase.Id, $"approved by {decision.By}: order {reference} sent to {purchase.Supplier}", "end:approved");
        return "approved " + reference;
    }

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> RejectPurchase(PurchaseOrder purchase, Decision decision)
    {
        await Report(purchase.Id, $"rejected by {decision.By} ({decision.Reason})", "end:rejected");
        return "rejected";
    }

    /// <summary>Nobody answered before the second deadline, which is different from a rejection.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> CancelForNoResponse(PurchaseOrder purchase)
    {
        await Report(purchase.Id, "cancelled: nobody answered before the deadlines", "end:cancelled");
        return "cancelled";
    }

    private Task Report(string orderId, string message, string step)
    {
        timeline.Add(orderId, message, step);
        return Task.CompletedTask;
    }
}
