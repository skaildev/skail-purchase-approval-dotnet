using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Web.Orders;

public enum OrderPhase
{
    Starting,
    WaitingForManager,
    WaitingForDirector,
    Approved,
    Rejected,
    Cancelled,
    Failed,
}

public enum LevelStatus
{
    NotRequested,
    Pending,
    Approved,
    Rejected,

    /// <summary>The deadline of this level passed without an answer.</summary>
    NoResponse,
}

public enum DecisionOutcome
{
    Accepted,
    Duplicate,

    /// <summary>The level is not the one waiting for a decision (not requested yet, or already escalated past).</summary>
    WrongLevel,
    Finished,
    NotFound,
}

/// <param name="DeadlineSeconds">The level's deadline, for the page's (visual only) countdown.</param>
/// <param name="RequestedAt">When the request reached the level: the start of the countdown.</param>
public sealed record LevelView(string Level, LevelStatus Status, int DeadlineSeconds, DateTimeOffset? RequestedAt, string? By, string? Reason, DateTimeOffset? At)
{
    public static LevelView New(string level, int deadlineSeconds) => new(level, LevelStatus.NotRequested, deadlineSeconds, null, null, null, null);
}

public sealed record TimelineEntry(string Kind, string? Level, string Text, DateTimeOffset At);

public sealed record OrderState(
    string OrderId,
    PurchaseOrder Purchase,
    OrderPhase Phase,
    LevelView Manager,
    LevelView Director,
    IReadOnlyList<TimelineEntry> Timeline,
    OrderResult? Result,
    string? Error)
{
    public bool IsTerminal => Phase is OrderPhase.Approved or OrderPhase.Rejected or OrderPhase.Cancelled or OrderPhase.Failed;
    public bool Active => !IsTerminal;

    public LevelView LevelOf(string level) => level == Levels.Manager ? Manager : Director;

    public OrderState WithLevel(string level, Func<LevelView, LevelView> change) =>
        level == Levels.Manager ? this with { Manager = change(Manager) } : this with { Director = change(Director) };
}
