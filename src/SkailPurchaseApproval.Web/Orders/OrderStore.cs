using System.Threading.Channels;
using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Web.Orders;

/// <summary>
/// In-memory state of the orders. The durable state lives in skail; this is only the UI view and the
/// "one decision, from the level that is waiting" guard. The decision is registered here when the click
/// arrives (before the fire), and the final result comes from the worker.
/// </summary>
public sealed class OrderStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, OrderState> _orders = [];
    private readonly Dictionary<string, List<Channel<OrderState>>> _subscribers = [];

    public OrderState Create(PurchaseOrder purchase, int managerDeadlineSeconds, int directorDeadlineSeconds)
    {
        lock (_gate)
        {
            var order = new OrderState(purchase.Id, purchase, OrderPhase.Starting,
                LevelView.New(Levels.Manager, managerDeadlineSeconds), LevelView.New(Levels.Director, directorDeadlineSeconds),
                [], null, null);
            _orders[purchase.Id] = order;
            return order;
        }
    }

    public OrderState? Get(string orderId)
    {
        lock (_gate) return _orders.GetValueOrDefault(orderId);
    }

    public void Remove(string orderId)
    {
        lock (_gate)
        {
            _orders.Remove(orderId);
            Complete(orderId);
        }
    }

    /// <summary>
    /// Registers a decision. Only the level that is waiting can decide, and the first decision wins: a decision
    /// from the other level, a second one, or one after the order finished is refused so no second event is fired.
    /// </summary>
    public DecisionOutcome TryDecide(string orderId, Decision decision)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order)) return DecisionOutcome.NotFound;
            if (order.IsTerminal) return DecisionOutcome.Finished;
            if (order.LevelOf(decision.Level).Status != LevelStatus.Pending)
            {
                var alreadyDecided = order.Manager.Status is LevelStatus.Approved or LevelStatus.Rejected
                    || order.Director.Status is LevelStatus.Approved or LevelStatus.Rejected;
                return alreadyDecided ? DecisionOutcome.Duplicate : DecisionOutcome.WrongLevel;
            }

            var status = decision.Approved ? LevelStatus.Approved : LevelStatus.Rejected;
            var verb = decision.Approved ? "approved" : "rejected";
            var reason = decision.Approved || string.IsNullOrWhiteSpace(decision.Reason) ? "" : $": {decision.Reason}";
            var next = order.WithLevel(decision.Level, l => l with { Status = status, By = decision.By, Reason = decision.Reason, At = decision.At });
            Publish(Add(next, status.ToString(), decision.Level, $"{Levels.Label(decision.Level)} {verb} ({decision.By}){reason}"));
            return DecisionOutcome.Accepted;
        }
    }

    /// <summary>Undoes <see cref="TryDecide"/> when the fire failed, so the same level can try again.</summary>
    public void Release(string orderId, string level)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order) || order.IsTerminal) return;
            if (order.LevelOf(level).Status is not (LevelStatus.Approved or LevelStatus.Rejected)) return;

            var timeline = order.Timeline.ToList();
            var last = timeline.FindLastIndex(t => t.Level == level && t.Kind is "Approved" or "Rejected");
            if (last >= 0) timeline.RemoveAt(last);
            Publish((order with { Timeline = timeline })
                .WithLevel(level, l => l with { Status = LevelStatus.Pending, By = null, Reason = null, At = null }));
        }
    }

    /// <returns>true when the event changed the order; false when ignored (unknown or finished order, duplicate).</returns>
    public bool Apply(OrderEvent evt)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(evt.OrderId, out var order) || order.IsTerminal) return false;

            OrderState? next = evt.Stage switch
            {
                OrderStage.Started when order.Phase == OrderPhase.Starting =>
                    Add(order with { Phase = OrderPhase.WaitingForManager }, "Started", null, "Order sent for approval"),
                OrderStage.ApprovalRequested when evt.Level == Levels.Manager && order.Manager.Status == LevelStatus.NotRequested =>
                    Add(order.WithLevel(Levels.Manager, l => l with { Status = LevelStatus.Pending, RequestedAt = _clock.GetUtcNow() }) with
                    {
                        Phase = order.Phase == OrderPhase.Starting ? OrderPhase.WaitingForManager : order.Phase,
                    }, "Requested", Levels.Manager, "Approval requested from the Manager"),
                OrderStage.Escalated when evt.Level == Levels.Director && order.Director.Status == LevelStatus.NotRequested => Escalate(order),
                OrderStage.Approved when evt.Result is { } r =>
                    Add(order with { Phase = OrderPhase.Approved, Result = r }, "Completed", null, $"Purchase approved ({r.Reference})"),
                OrderStage.Rejected when evt.Result is { } r =>
                    Add(order with { Phase = OrderPhase.Rejected, Result = r }, "Completed", null,
                        $"Purchase rejected by the {Levels.Label(r.Decision?.Level ?? "")}" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason})")),
                OrderStage.Cancelled when evt.Result is { } r => Cancel(order, r),
                OrderStage.Failed => Add(order with { Phase = OrderPhase.Failed, Error = evt.Error ?? "unknown error" }, "Failed", null, evt.Error ?? "unknown error"),
                _ => null,
            };
            if (next is null) return false;

            Publish(next);
            return true;
        }
    }

    public Subscription Subscribe(string orderId)
    {
        var channel = Channel.CreateUnbounded<OrderState>();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(orderId, out var list)) _subscribers[orderId] = list = [];
            list.Add(channel);
        }
        return new Subscription(channel.Reader, () =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(orderId, out var list)) list.Remove(channel);
            }
        });
    }

    private OrderState Escalate(OrderState order)
    {
        // a manager decision that raced the deadline keeps its status (skail consumes it at the second wait);
        // otherwise the manager did not answer and the director is now the one waiting
        if (order.Manager.Status is LevelStatus.Approved or LevelStatus.Rejected)
            return Add(order, "Escalated", Levels.Director, "Deadline passed; the Manager's decision arrived in time");

        var next = order.WithLevel(Levels.Manager, l => l with { Status = LevelStatus.NoResponse })
            .WithLevel(Levels.Director, l => l with { Status = LevelStatus.Pending, RequestedAt = _clock.GetUtcNow() });
        return Add(next with { Phase = OrderPhase.WaitingForDirector }, "Escalated", Levels.Director,
            "No response from the Manager: escalated to the Director");
    }

    private OrderState Cancel(OrderState order, OrderResult r)
    {
        // whoever was still waiting never answered
        LevelView NoAnswer(LevelView l) => l.Status == LevelStatus.Pending ? l with { Status = LevelStatus.NoResponse } : l;
        var next = order with { Phase = OrderPhase.Cancelled, Result = r, Manager = NoAnswer(order.Manager), Director = NoAnswer(order.Director) };
        return Add(next, "Cancelled", null, "Order cancelled: no response from the Manager or the Director");
    }

    private OrderState Add(OrderState order, string kind, string? level, string text) =>
        order with { Timeline = [.. order.Timeline, new TimelineEntry(kind, level, text, _clock.GetUtcNow())] };

    // must be called with _gate held
    private void Publish(OrderState state)
    {
        _orders[state.OrderId] = state;
        if (_subscribers.TryGetValue(state.OrderId, out var list))
            foreach (var channel in list) channel.Writer.TryWrite(state);
        if (state.IsTerminal) Complete(state.OrderId);
    }

    private void Complete(string orderId)
    {
        if (!_subscribers.TryGetValue(orderId, out var list)) return;
        foreach (var channel in list) channel.Writer.TryComplete();
    }

    public sealed class Subscription(ChannelReader<OrderState> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<OrderState> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
