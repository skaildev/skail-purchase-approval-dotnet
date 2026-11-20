namespace SkailPurchaseApproval;

/// <summary>The two approval levels, as plain strings: they travel in the payload of an external event.</summary>
public static class Levels
{
    public const string Manager = "manager";
    public const string Director = "director";

    public static string Label(string level) => level == Manager ? "Manager" : "Director";
}

/// <summary>External event name, shared by the function (WaitForEvent) and the console (fire).</summary>
public static class Events
{
    public const string PurchaseDecision = "PURCHASE_DECISION";
}

/// <summary>The purchase order that goes into the run history: no sensitive data.</summary>
public sealed record PurchaseOrder(string Id, string Supplier, string Description, decimal Amount);

/// <summary>
/// One level's answer, and the payload of the external event. At is stamped by whoever fires the event, since the
/// function cannot read the clock.
/// </summary>
public sealed record Decision(string Level, bool Approved, string By, string? Reason, DateTimeOffset At);
