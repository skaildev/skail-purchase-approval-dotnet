using Microsoft.Extensions.Logging.Abstractions;
using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Worker;
using SkailPurchaseApproval.Worker.Progress;

namespace SkailPurchaseApproval.Tests;

public sealed class RecordingNotifier : IProgressNotifier
{
    public List<OrderEvent> Events { get; } = [];
    public Exception? FailWith { get; set; }

    public Task NotifyAsync(OrderEvent evt, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>Commands need the skail runtime; the logic they delegate to is tested here.</summary>
public class OperationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PurchaseOrder Order = new("abcdef12-0000", "Acme", "10 notebooks", 25000m);
    private static Decision Approved(string level = Levels.Manager) => new(level, true, "Ana", null, T0);
    private static Decision Rejected(string level = Levels.Manager) => new(level, false, "Beto", "fora do orçamento", T0);

    private static (PurchaseOperations Ops, RecordingNotifier Notifier) Create()
    {
        var notifier = new RecordingNotifier();
        return (new PurchaseOperations(notifier, NullLogger<PurchaseOperations>.Instance), notifier);
    }

    [Fact]
    public async Task RequestApproval_to_the_manager_delivers_an_approval_request()
    {
        var (ops, notifier) = Create();

        await ops.RequestApprovalAsync(Order, Levels.Manager);

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((OrderStage.ApprovalRequested, Levels.Manager, Order.Id), (evt.Stage, evt.Level, evt.OrderId));
    }

    [Fact]
    public async Task RequestApproval_to_the_director_is_the_escalation()
    {
        var (ops, notifier) = Create();

        await ops.RequestApprovalAsync(Order, Levels.Director);

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((OrderStage.Escalated, Levels.Director), (evt.Stage, evt.Level));
    }

    [Fact]
    public async Task RequestApproval_rejects_an_unknown_level_without_notifying()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => ops.RequestApprovalAsync(Order, "rh"));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task RequestApproval_throws_when_the_delivery_fails_so_skail_retries()
    {
        var (ops, notifier) = Create();
        notifier.FailWith = new HttpRequestException("web down");

        await Assert.ThrowsAsync<HttpRequestException>(() => ops.RequestApprovalAsync(Order, Levels.Manager));
    }

    [Fact]
    public async Task Confirm_returns_the_reference_and_notifies_approved()
    {
        var (ops, notifier) = Create();

        var result = await ops.ConfirmAsync(Order, Approved(Levels.Director));

        Assert.Equal(OrderEnd.Approved, result.End);
        Assert.Equal("PO-ABCDEF12", result.Reference);
        var evt = Assert.Single(notifier.Events);
        Assert.Equal(OrderStage.Approved, evt.Stage);
        Assert.Equal(result.Reference, evt.Result!.Reference);
    }

    [Fact]
    public async Task Confirm_refuses_a_rejection_without_notifying()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.ConfirmAsync(Order, Rejected()));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task Confirm_is_idempotent_for_the_result()
    {
        var (ops, _) = Create();

        var a = await ops.ConfirmAsync(Order, Approved());
        var b = await ops.ConfirmAsync(Order, Approved());

        Assert.Equal(a.Reference, b.Reference);
    }

    [Fact]
    public async Task Reject_reports_the_level_and_the_reason()
    {
        var (ops, notifier) = Create();

        var result = await ops.RejectAsync(Order, Rejected(Levels.Director));

        Assert.Equal(OrderEnd.Rejected, result.End);
        Assert.Equal((Levels.Director, "fora do orçamento"), (result.Decision!.Level, result.Reason));
        Assert.Equal(OrderStage.Rejected, Assert.Single(notifier.Events).Stage);
    }

    [Fact]
    public async Task Reject_refuses_an_approval_without_notifying()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RejectAsync(Order, Approved()));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task Reject_is_idempotent_for_the_result()
    {
        var (ops, _) = Create();

        var a = await ops.RejectAsync(Order, Rejected());
        var b = await ops.RejectAsync(Order, Rejected());

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task CancelForNoResponse_cancels_and_notifies()
    {
        var (ops, notifier) = Create();

        var result = await ops.CancelForNoResponseAsync(Order);

        Assert.Equal(OrderEnd.Cancelled, result.End);
        Assert.Equal("no response", result.Reason);
        var evt = Assert.Single(notifier.Events);
        Assert.Equal(OrderStage.Cancelled, evt.Stage);
        Assert.Equal(OrderEnd.Cancelled, evt.Result!.End);
    }

    [Fact]
    public async Task CancelForNoResponse_is_idempotent_for_the_result()
    {
        var (ops, _) = Create();

        Assert.Equal(await ops.CancelForNoResponseAsync(Order), await ops.CancelForNoResponseAsync(Order));
    }

    [Fact]
    public async Task ReportStatus_forwards_the_event()
    {
        var (ops, notifier) = Create();

        await ops.ReportStatusAsync(OrderEvent.Started(Order));

        Assert.Equal(OrderStage.Started, Assert.Single(notifier.Events).Stage);
    }
}
