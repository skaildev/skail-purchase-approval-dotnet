using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Worker;

public class PurchaseFunctions(PurchaseCommands commands)
{
    /// <summary>
    /// Asks the manager to approve the order and waits for the decision until a deadline. With no answer it escalates
    /// to the director and waits again; with no answer again, it cancels. The deadlines are arguments (recorded in the
    /// history), so the same code serves the demo (1 minute) and a real approval (48 hours).
    /// </summary>
    [SkailFunction]
    public async SkailTask<OrderResult> ApprovePurchase(PurchaseOrder purchase, int managerDeadlineSeconds, int directorDeadlineSeconds)
    {
        try
        {
            await commands.ReportStatus(OrderEvent.Started(purchase));
            await commands.RequestApproval(purchase, Levels.Manager);

            // The wait is created once, without await, and reused in both WhenAny.
            var decision = SkailTask.WaitForEvent<Decision>(Events.PurchaseDecision, purchase.Id);

            // WhenAny returns the task that finished first. Decide by it, not by decision.IsCompleted: on a resume,
            // IsCompleted already sees a decision that arrived after the deadline.
            var winner = await SkailTask.WhenAny(decision, SkailTask.Delay(TimeSpan.FromSeconds(managerDeadlineSeconds)));
            if (winner != decision)
            {
                await commands.RequestApproval(purchase, Levels.Director);   // escalation
                winner = await SkailTask.WhenAny(decision, SkailTask.Delay(TimeSpan.FromSeconds(directorDeadlineSeconds)));
            }

            if (winner != decision)
                return await commands.CancelForNoResponse(purchase);

            var answer = await decision;
            return answer.Approved
                ? await commands.ConfirmPurchase(purchase, answer)
                : await commands.RejectPurchase(purchase, answer);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            await commands.ReportStatus(OrderEvent.Failed(purchase.Id, ex.Message));
            throw;
        }
    }
}
