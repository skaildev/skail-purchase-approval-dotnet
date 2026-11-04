using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Web.Orders;

namespace SkailPurchaseApproval.Tests;

public class OrderStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PurchaseOrder Order = new("o1", "Acme", "10 notebooks", 25000m);
    private static Decision Approved(string level, string by = "Ana") => new(level, true, by, null, T0);
    private static Decision Rejected(string level) => new(level, false, "Beto", "fora do orçamento", T0);

    private static OrderStore NewStore(out OrderState created)
    {
        var store = new OrderStore();
        created = store.Create(Order, managerDeadlineSeconds: 60, directorDeadlineSeconds: 90);
        return store;
    }

    private static OrderStore WaitingForManager()
    {
        var store = NewStore(out _);
        store.Apply(OrderEvent.Started(Order));
        store.Apply(OrderEvent.ApprovalRequested("o1", Levels.Manager));
        return store;
    }

    private static OrderStore WaitingForDirector()
    {
        var store = WaitingForManager();
        store.Apply(OrderEvent.Escalated("o1", Levels.Director));
        return store;
    }

    [Fact]
    public void A_new_order_is_starting_with_nothing_requested_and_keeps_the_deadlines()
    {
        NewStore(out var o);

        Assert.Equal(OrderPhase.Starting, o.Phase);
        Assert.True(o.Active);
        Assert.Equal(LevelStatus.NotRequested, o.Manager.Status);
        Assert.Equal(LevelStatus.NotRequested, o.Director.Status);
        Assert.Equal((60, 90), (o.Manager.DeadlineSeconds, o.Director.DeadlineSeconds));
        Assert.Equal(Order, o.Purchase);
    }

    [Fact]
    public void Started_and_the_manager_request_make_the_manager_pending_with_a_request_time()
    {
        var store = WaitingForManager();

        var o = store.Get("o1")!;
        Assert.Equal(OrderPhase.WaitingForManager, o.Phase);
        Assert.Equal(LevelStatus.Pending, o.Manager.Status);
        Assert.NotNull(o.Manager.RequestedAt);
        Assert.Equal(LevelStatus.NotRequested, o.Director.Status);
    }

    [Fact]
    public void Duplicated_events_are_ignored()
    {
        var store = WaitingForManager();

        Assert.False(store.Apply(OrderEvent.Started(Order)));
        Assert.False(store.Apply(OrderEvent.ApprovalRequested("o1", Levels.Manager)));
    }

    [Fact]
    public void Escalation_marks_the_manager_as_no_response_and_asks_the_director()
    {
        var store = WaitingForDirector();

        var o = store.Get("o1")!;
        Assert.Equal(OrderPhase.WaitingForDirector, o.Phase);
        Assert.Equal(LevelStatus.NoResponse, o.Manager.Status);
        Assert.Equal(LevelStatus.Pending, o.Director.Status);
        Assert.NotNull(o.Director.RequestedAt);
    }

    [Fact]
    public void A_repeated_escalation_is_ignored()
    {
        var store = WaitingForDirector();

        Assert.False(store.Apply(OrderEvent.Escalated("o1", Levels.Director)));
    }

    [Fact]
    public void The_manager_decision_is_recorded_with_who_and_why()
    {
        var store = WaitingForManager();

        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("o1", Rejected(Levels.Manager)));

        var level = store.Get("o1")!.Manager;
        Assert.Equal((LevelStatus.Rejected, "Beto", "fora do orçamento"), (level.Status, level.By, level.Reason));
    }

    [Fact]
    public void A_manager_decision_after_the_escalation_is_the_wrong_level()
    {
        var store = WaitingForDirector();

        Assert.Equal(DecisionOutcome.WrongLevel, store.TryDecide("o1", Approved(Levels.Manager)));

        Assert.Equal(LevelStatus.NoResponse, store.Get("o1")!.Manager.Status);
    }

    [Fact]
    public void A_director_decision_before_the_escalation_is_the_wrong_level()
    {
        var store = WaitingForManager();

        Assert.Equal(DecisionOutcome.WrongLevel, store.TryDecide("o1", Approved(Levels.Director)));
    }

    [Fact]
    public void The_director_can_decide_after_the_escalation()
    {
        var store = WaitingForDirector();

        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("o1", Approved(Levels.Director)));

        Assert.Equal(LevelStatus.Approved, store.Get("o1")!.Director.Status);
    }

    [Fact]
    public void A_second_decision_for_the_same_order_is_a_duplicate_and_the_first_prevails()
    {
        var store = WaitingForManager();
        store.TryDecide("o1", Approved(Levels.Manager, "Ana"));

        Assert.Equal(DecisionOutcome.Duplicate, store.TryDecide("o1", Rejected(Levels.Manager)));

        var level = store.Get("o1")!.Manager;
        Assert.Equal((LevelStatus.Approved, "Ana"), (level.Status, level.By));
    }

    [Fact]
    public void A_decision_of_the_other_level_after_a_decision_is_also_refused()
    {
        var store = WaitingForManager();
        store.TryDecide("o1", Approved(Levels.Manager));

        Assert.NotEqual(DecisionOutcome.Accepted, store.TryDecide("o1", Approved(Levels.Director)));
    }

    [Fact]
    public void A_decision_after_the_order_finished_is_rejected()
    {
        var store = WaitingForManager();
        store.Apply(OrderEvent.Failed("o1", "boom"));

        Assert.Equal(DecisionOutcome.Finished, store.TryDecide("o1", Approved(Levels.Manager)));
    }

    [Fact]
    public void Deciding_an_unknown_order_is_not_found()
    {
        Assert.Equal(DecisionOutcome.NotFound, new OrderStore().TryDecide("nope", Approved(Levels.Manager)));
    }

    [Fact]
    public void Release_puts_the_level_back_to_pending_so_the_fire_can_be_retried()
    {
        var store = WaitingForManager();
        store.TryDecide("o1", Approved(Levels.Manager));

        store.Release("o1", Levels.Manager);

        var level = store.Get("o1")!.Manager;
        Assert.Equal(LevelStatus.Pending, level.Status);
        Assert.NotNull(level.RequestedAt);
        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("o1", Rejected(Levels.Manager)));
    }

    [Fact]
    public void Approved_event_is_terminal_with_the_result()
    {
        var store = WaitingForManager();
        store.TryDecide("o1", Approved(Levels.Manager));

        store.Apply(OrderEvent.Approved("o1", OrderResult.Approved("o1", Approved(Levels.Manager))));

        var o = store.Get("o1")!;
        Assert.Equal(OrderPhase.Approved, o.Phase);
        Assert.False(o.Active);
        Assert.Equal("PO-O1", o.Result!.Reference);
    }

    [Fact]
    public void Rejected_event_is_terminal()
    {
        var store = WaitingForManager();
        store.TryDecide("o1", Rejected(Levels.Manager));

        store.Apply(OrderEvent.Rejected("o1", OrderResult.Rejected("o1", Rejected(Levels.Manager))));

        Assert.Equal(OrderPhase.Rejected, store.Get("o1")!.Phase);
    }

    [Fact]
    public void Cancelled_after_both_deadlines_marks_both_levels_as_no_response()
    {
        var store = WaitingForDirector();

        store.Apply(OrderEvent.Cancelled("o1", OrderResult.NoResponse("o1")));

        var o = store.Get("o1")!;
        Assert.Equal(OrderPhase.Cancelled, o.Phase);
        Assert.Equal((LevelStatus.NoResponse, LevelStatus.NoResponse), (o.Manager.Status, o.Director.Status));
        Assert.Equal(OrderEnd.Cancelled, o.Result!.End);
    }

    [Fact]
    public void Failed_event_is_terminal_with_the_error_and_later_events_are_ignored()
    {
        var store = WaitingForManager();

        store.Apply(OrderEvent.Failed("o1", "boom"));

        Assert.False(store.Apply(OrderEvent.Escalated("o1", Levels.Director)));
        var o = store.Get("o1")!;
        Assert.Equal((OrderPhase.Failed, "boom"), (o.Phase, o.Error));
    }

    [Fact]
    public void Timeline_lists_the_events_in_the_order_they_arrived()
    {
        var store = WaitingForDirector();
        store.TryDecide("o1", Approved(Levels.Director));
        store.Apply(OrderEvent.Approved("o1", OrderResult.Approved("o1", Approved(Levels.Director))));

        var kinds = store.Get("o1")!.Timeline.Select(t => t.Kind).ToArray();

        Assert.Equal(["Started", "Requested", "Escalated", "Approved", "Completed"], kinds);
    }

    [Fact]
    public async Task Subscribers_receive_each_update_and_the_channel_closes_when_terminal()
    {
        var store = NewStore(out _);
        using var sub = store.Subscribe("o1");

        store.Apply(OrderEvent.Started(Order));
        store.Apply(OrderEvent.ApprovalRequested("o1", Levels.Manager));
        store.Apply(OrderEvent.Failed("o1", "boom"));

        var phases = new List<OrderPhase>();
        await foreach (var s in sub.Reader.ReadAllAsync()) phases.Add(s.Phase);

        Assert.Equal([OrderPhase.WaitingForManager, OrderPhase.WaitingForManager, OrderPhase.Failed], phases);
    }

    [Fact]
    public void Remove_forgets_the_order()
    {
        var store = NewStore(out _);

        store.Remove("o1");

        Assert.Null(store.Get("o1"));
    }
}
