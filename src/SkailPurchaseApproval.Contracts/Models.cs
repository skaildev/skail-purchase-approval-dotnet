namespace SkailPurchaseApproval.Contracts;

/// <summary>
/// The two approval levels. They are plain strings (not an enum) because <see cref="Decision"/> is the
/// payload of an external event: strings survive any JSON setup on the skail side.
/// </summary>
public static class Levels
{
    public const string Manager = "manager";
    public const string Director = "director";

    public static bool IsValid(string? level) => level is Manager or Director;

    /// <summary>English label used by the page and by the default approver name.</summary>
    public static string Label(string level) => level == Manager ? "Manager" : "Director";
}

/// <summary>External event name, shared by the function (WaitForEvent) and the web app (fire).</summary>
public static class Events
{
    public const string PurchaseDecision = "PURCHASE_DECISION";
}

public static class OrderLimits
{
    public const string DefaultSupplier = "Papelaria Central";
    public const string DefaultDescription = "A4 paper reams (50 boxes)";
    public const decimal DefaultAmount = 18500m;
    public const int MaxTextLength = 120;

    /// <summary>Deadline of each level in the demo; the original use case is 48 hours.</summary>
    public const int DefaultDeadlineSeconds = 60;
}

/// <summary>The purchase order that goes into the function's history: no sensitive data.</summary>
public sealed record PurchaseOrder(string Id, string Supplier, string Description, decimal Amount);

/// <summary>
/// One level's answer. It is the payload of the external event, so it only carries what the decision needs:
/// <c>Em</c> is stamped by the web app, since the function cannot read the clock.
/// </summary>
public sealed record Decision(string Level, bool Approved, string By, string? Reason, DateTimeOffset At);

public enum OrderEnd
{
    Approved,
    Rejected,

    /// <summary>Nobody answered before the second deadline (different from an explicit rejection).</summary>
    Cancelled,
}

public sealed record OrderResult(string OrderId, OrderEnd End, string? Reference, Decision? Decision, string? Reason)
{
    public const string NoResponseReason = "no response";

    /// <summary>The reference derives from the id, so a retried command returns the same one.</summary>
    public static OrderResult Approved(string orderId, Decision decision) =>
        new(orderId, OrderEnd.Approved, ReferenceFor(orderId), decision, null);

    public static OrderResult Rejected(string orderId, Decision decision) =>
        new(orderId, OrderEnd.Rejected, null, decision, decision.Reason);

    public static OrderResult NoResponse(string orderId) =>
        new(orderId, OrderEnd.Cancelled, null, null, NoResponseReason);

    private static string ReferenceFor(string orderId) =>
        "PO-" + new string(orderId.Take(8).ToArray()).ToUpperInvariant();
}

public enum OrderStage
{
    Started,
    ApprovalRequested,
    Escalated,
    Approved,
    Rejected,
    Cancelled,
    Failed,
}

/// <summary>Progress reported by the worker to the web app. The decisions themselves go straight to skail.</summary>
public sealed record OrderEvent(
    string OrderId,
    OrderStage Stage,
    PurchaseOrder? Purchase,
    string? Level,
    OrderResult? Result,
    string? Error)
{
    public static OrderEvent Started(PurchaseOrder purchase) => new(purchase.Id, OrderStage.Started, purchase, null, null, null);

    public static OrderEvent ApprovalRequested(string orderId, string level) =>
        new(orderId, OrderStage.ApprovalRequested, null, level, null, null);

    public static OrderEvent Escalated(string orderId, string level) =>
        new(orderId, OrderStage.Escalated, null, level, null, null);

    public static OrderEvent Approved(string orderId, OrderResult result) =>
        new(orderId, OrderStage.Approved, null, null, result, null);

    public static OrderEvent Rejected(string orderId, OrderResult result) =>
        new(orderId, OrderStage.Rejected, null, null, result, null);

    public static OrderEvent Cancelled(string orderId, OrderResult result) =>
        new(orderId, OrderStage.Cancelled, null, null, result, null);

    public static OrderEvent Failed(string orderId, string error) =>
        new(orderId, OrderStage.Failed, null, null, null, error);
}
