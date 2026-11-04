using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Web.Skail;

public sealed class SkailGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISkailGateway
{
    /// <summary>
    /// Starts the ApprovePurchase function; <c>purchase.Id</c> is the execution id. The deadlines are arguments
    /// of the function (it cannot read configuration).
    /// </summary>
    Task TriggerAsync(PurchaseOrder purchase, int managerDeadlineSeconds, int directorDeadlineSeconds, CancellationToken ct = default);

    /// <summary>Fires the external event the function waits on; the instance id is the order id.</summary>
    Task FireDecisionAsync(string orderId, Decision decision, CancellationToken ct = default);
}
