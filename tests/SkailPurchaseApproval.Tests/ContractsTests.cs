using System.Text.Json;
using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Tests;

public class ContractsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PurchaseOrder Order = new("abcdef12-0000", "Acme", "10 notebooks", 25000m);
    private static Decision Approved(string level = Levels.Manager) => new(level, true, "Ana", null, T0);
    private static Decision Rejected(string level = Levels.Manager) => new(level, false, "Beto", "fora do orçamento", T0);

    [Fact]
    public void Event_name_is_a_shared_constant()
    {
        Assert.Equal("PURCHASE_DECISION", Events.PurchaseDecision);
    }

    [Theory]
    [InlineData("manager", true)]
    [InlineData("director", true)]
    [InlineData("Manager", false)]
    [InlineData("rh", false)]
    [InlineData("", false)]
    public void Only_the_two_levels_are_valid(string level, bool valid)
    {
        Assert.Equal(valid, Levels.IsValid(level));
    }

    [Fact]
    public void Levels_have_english_labels()
    {
        Assert.Equal("Manager", Levels.Label(Levels.Manager));
        Assert.Equal("Director", Levels.Label(Levels.Director));
    }

    [Fact]
    public void Approved_result_carries_the_decision_and_a_stable_reference()
    {
        var a = OrderResult.Approved(Order.Id, Approved());
        var b = OrderResult.Approved(Order.Id, Approved());

        Assert.Equal(OrderEnd.Approved, a.End);
        Assert.Equal("PO-ABCDEF12", a.Reference);
        Assert.Equal(a.Reference, b.Reference); // a retry of the command must not invent another reference
        Assert.Equal(Levels.Manager, a.Decision!.Level);
        Assert.Null(a.Reason);
    }

    [Fact]
    public void Rejected_result_has_the_level_and_the_reason_and_no_reference()
    {
        var r = OrderResult.Rejected(Order.Id, Rejected(Levels.Director));

        Assert.Equal(OrderEnd.Rejected, r.End);
        Assert.Null(r.Reference);
        Assert.Equal(Levels.Director, r.Decision!.Level);
        Assert.Equal("fora do orçamento", r.Reason);
    }

    [Fact]
    public void Cancelled_for_no_response_is_not_a_rejection()
    {
        var r = OrderResult.NoResponse(Order.Id);

        Assert.Equal(OrderEnd.Cancelled, r.End);
        Assert.Null(r.Decision);
        Assert.Equal("no response", r.Reason);
        Assert.Null(r.Reference);
    }

    [Fact]
    public void Order_and_decision_payloads_round_trip_with_the_shared_json_options()
    {
        var order = JsonSerializer.Deserialize<PurchaseOrder>(JsonSerializer.Serialize(Order, Json.Options), Json.Options);
        var decision = JsonSerializer.Deserialize<Decision>(JsonSerializer.Serialize(Rejected(), Json.Options), Json.Options);

        Assert.Equal(Order, order);
        Assert.Equal(Rejected(), decision);
    }

    [Fact]
    public void Order_event_round_trips_with_the_result()
    {
        var evt = OrderEvent.Approved("c", OrderResult.Approved("c", Approved()));

        var back = JsonSerializer.Deserialize<OrderEvent>(JsonSerializer.Serialize(evt, Json.Options), Json.Options)!;

        Assert.Equal(OrderStage.Approved, back.Stage);
        Assert.Equal(evt.Result!.Reference, back.Result!.Reference);
        Assert.Equal(Levels.Manager, back.Result.Decision!.Level);
    }

    [Fact]
    public void Event_factories_set_stage_level_order_and_error()
    {
        var started = OrderEvent.Started(Order);
        Assert.Equal((OrderStage.Started, Order.Id), (started.Stage, started.OrderId));
        Assert.Equal(Order, started.Purchase);

        var requested = OrderEvent.ApprovalRequested("c", Levels.Manager);
        Assert.Equal((OrderStage.ApprovalRequested, Levels.Manager), (requested.Stage, requested.Level));

        var escalated = OrderEvent.Escalated("c", Levels.Director);
        Assert.Equal((OrderStage.Escalated, Levels.Director), (escalated.Stage, escalated.Level));

        Assert.Equal(OrderStage.Rejected, OrderEvent.Rejected("c", OrderResult.Rejected("c", Rejected())).Stage);
        Assert.Equal(OrderStage.Cancelled, OrderEvent.Cancelled("c", OrderResult.NoResponse("c")).Stage);
        Assert.Equal("boom", OrderEvent.Failed("c", "boom").Error);
    }
}
