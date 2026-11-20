using SkailPurchaseApproval.Skail;

namespace SkailPurchaseApproval.Demo;

/// <summary>
/// Plays the approvers for the page: it places the purchase order and turns each button into the decision event. Only
/// the level the order is waiting for can decide, and only once. It follows one order at a time, the last one placed
/// (or the one given on the command line).
/// </summary>
public sealed class PurchaseDesk(SkailApiClient skail, Timeline timeline)
{
    /// <summary>Deadline of each level: 1 minute in the demo; a real approval would use 48 hours.</summary>
    public const int DeadlineSeconds = 60;

    private static readonly PurchaseOrder Sample = new("", "Papelaria Central", "A4 paper reams (50 boxes)", 18500m);

    private readonly object gate = new();
    private PurchaseOrder? order;
    private bool decided;

    /// <summary>Follows an order that already exists, to see it resume.</summary>
    public void Follow(string id)
    {
        lock (gate) { order = Sample with { Id = id }; decided = false; }
    }

    public async Task<string> PlaceAsync(CancellationToken ct)
    {
        var placed = Sample with { Id = Guid.NewGuid().ToString() };
        await skail.TriggerAsync(placed, DeadlineSeconds, DeadlineSeconds, ct);
        lock (gate) { order = placed; decided = false; }
        return placed.Id;
    }

    /// <summary>Sends the decision of the level the order is waiting for. The first decision wins.</summary>
    public async Task<bool> DecideAsync(string level, bool approved, CancellationToken ct)
    {
        string id;
        lock (gate)
        {
            if (order is null || decided || Waiting(timeline.For(order.Id)) != level) return false;
            decided = true;
            id = order.Id;
        }

        var decision = new Decision(level, approved, Levels.Label(level), approved ? null : "over the budget", DateTimeOffset.UtcNow);
        await skail.FireDecisionAsync(id, decision, ct);
        timeline.Add(id, $"{Levels.Label(level)} {(approved ? "approved" : "rejected")}: {Events.PurchaseDecision} sent", $"sent:{level}");
        return true;
    }

    /// <summary>What the page shows: the order, each level and the timeline of the run.</summary>
    public object View()
    {
        lock (gate)
        {
            if (order is null) return new { order = (object?)null };

            var entries = timeline.For(order.Id);
            var end = entries.LastOrDefault(e => e.Step?.StartsWith("end:") == true)?.Step?[4..];
            var waiting = end is null && !decided ? Waiting(entries) : null;
            var askedAt = entries.LastOrDefault(e => e.Step == $"requested:{waiting}")?.At;
            return new
            {
                order = new
                {
                    id = order.Id,
                    order.Supplier,
                    order.Description,
                    order.Amount,
                    waiting,
                    deadline = askedAt?.AddSeconds(DeadlineSeconds),
                    end,
                },
                levels = new[] { Levels.Manager, Levels.Director }.Select(level => new
                {
                    level,
                    label = Levels.Label(level),
                    status = LevelStatus(level, entries, waiting),
                    canDecide = waiting == level,
                }),
                timeline = entries.Select(e => new { at = e.At, text = e.Text }),
            };
        }
    }

    /// <summary>The level asked last, which is the one that can decide now.</summary>
    private static string? Waiting(IReadOnlyList<TimelineEntry> entries) =>
        entries.LastOrDefault(e => e.Step?.StartsWith("requested:") == true)?.Step?[10..];

    private static string LevelStatus(string level, IReadOnlyList<TimelineEntry> entries, string? waiting)
    {
        if (entries.Any(e => e.Step == $"sent:{level}"))
            return entries.Any(e => e.Step == "end:approved") ? "approved" : entries.Any(e => e.Step == "end:rejected") ? "rejected" : "decided";
        if (waiting == level) return "waiting";
        if (entries.Any(e => e.Step == $"requested:{level}")) return "no answer";
        return "not asked";
    }
}
