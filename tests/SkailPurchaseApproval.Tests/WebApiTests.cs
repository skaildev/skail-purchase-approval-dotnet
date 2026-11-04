using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Web.Skail;

namespace SkailPurchaseApproval.Tests;

public sealed class FakeGateway : ISkailGateway
{
    public List<(PurchaseOrder Purchase, int ManagerDeadline, int DirectorDeadline)> Triggers { get; } = [];
    public List<(string OrderId, Decision Decision)> Fires { get; } = [];
    public Exception? FailWith { get; set; }
    public Exception? FailFireWith { get; set; }

    public Task TriggerAsync(PurchaseOrder purchase, int managerDeadlineSeconds, int directorDeadlineSeconds, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Triggers.Add((purchase, managerDeadlineSeconds, directorDeadlineSeconds));
        return Task.CompletedTask;
    }

    public Task FireDecisionAsync(string orderId, Decision decision, CancellationToken ct = default)
    {
        if (FailFireWith is not null) throw FailFireWith;
        Fires.Add((orderId, decision));
        return Task.CompletedTask;
    }
}

public sealed class WebFactory : WebApplicationFactory<Program>
{
    public FakeGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ISkailGateway>();
            s.AddSingleton<ISkailGateway>(Gateway);
            s.RemoveAll<ApprovalOptions>();
            s.AddSingleton(new ApprovalOptions { ManagerDeadlineSeconds = 7, DirectorDeadlineSeconds = 9 });
        });
    }
}

public class WebApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;
    private readonly HttpClient _http;

    public WebApiTests(WebFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    private async Task<JsonElement> StartAsync(object? body = null)
    {
        var res = await _http.PostAsJsonAsync("/api/orders", body ?? new { });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> StartIdAsync(object? body = null) => (await StartAsync(body)).GetProperty("orderId").GetString()!;

    /// <summary>Starts an order and makes the manager pending, as the worker's first commands would.</summary>
    private async Task<string> StartRequestedAsync()
    {
        var id = await StartIdAsync();
        await PostEventAsync(id, OrderEvent.Started(new PurchaseOrder(id, "Acme", "x", 1m)));
        await PostEventAsync(id, OrderEvent.ApprovalRequested(id, Levels.Manager));
        return id;
    }

    private Task<HttpResponseMessage> DecideAsync(string id, string level, bool approved = true, string? by = "Ana", string? reason = null) =>
        _http.PostAsJsonAsync($"/api/orders/{id}/decision", new { level, approved, by, reason });

    private Task<HttpResponseMessage> PostEventAsync(string id, OrderEvent evt) =>
        _http.PostAsJsonAsync($"/internal/orders/{id}/events", evt, Json.Options);

    [Fact]
    public async Task Health_endpoint_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Index_page_is_served_with_the_form_and_the_two_levels()
    {
        var html = await _http.GetStringAsync("/");

        Assert.Contains("id=\"supplier\"", html);
        Assert.Contains("id=\"description\"", html);
        Assert.Contains("id=\"amount\"", html);
        Assert.Contains("id=\"start\"", html);
        Assert.Contains("data-level=\"manager\"", html);
        Assert.Contains("data-level=\"director\"", html);
    }

    [Fact]
    public async Task Start_without_a_body_uses_the_defaults_and_triggers_skail_with_the_configured_deadlines()
    {
        var o = await StartAsync();

        var purchase = o.GetProperty("purchase");
        Assert.Equal(OrderLimits.DefaultSupplier, purchase.GetProperty("supplier").GetString());
        Assert.Equal(OrderLimits.DefaultDescription, purchase.GetProperty("description").GetString());
        Assert.Equal(OrderLimits.DefaultAmount, purchase.GetProperty("amount").GetDecimal());
        Assert.True(o.GetProperty("active").GetBoolean());
        Assert.Equal("Starting", o.GetProperty("phase").GetString());
        var id = o.GetProperty("orderId").GetString();
        var trigger = Assert.Single(_factory.Gateway.Triggers, t => t.Purchase.Id == id);
        Assert.Equal((7, 9), (trigger.ManagerDeadline, trigger.DirectorDeadline));
    }

    [Fact]
    public async Task Start_uses_the_trimmed_informed_values()
    {
        var o = await StartAsync(new { supplier = "  Dell  ", description = " 3 monitores ", amount = 1234.56m });

        var purchase = o.GetProperty("purchase");
        Assert.Equal(("Dell", "3 monitores", 1234.56m),
            (purchase.GetProperty("supplier").GetString(), purchase.GetProperty("description").GetString(), purchase.GetProperty("amount").GetDecimal()));
        Assert.Equal("Dell", _factory.Gateway.Triggers.Last().Purchase.Supplier);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Start_rejects_an_amount_that_is_not_positive(decimal amount)
    {
        var res = await _http.PostAsJsonAsync("/api/orders", new { amount });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Start_rejects_texts_that_are_too_long()
    {
        var long_ = new string('x', OrderLimits.MaxTextLength + 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/orders", new { supplier = long_ })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/orders", new { description = long_ })).StatusCode);
    }

    [Fact]
    public async Task Start_failure_returns_502_and_forgets_the_order()
    {
        _factory.Gateway.FailWith = new SkailGatewayException("skail answered 401");
        try
        {
            var res = await _http.PostAsJsonAsync("/api/orders", new { });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Contains("401", await res.Content.ReadAsStringAsync());
        }
        finally { _factory.Gateway.FailWith = null; }
    }

    [Fact]
    public async Task Get_unknown_order_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/orders/nope")).StatusCode);
    }

    [Fact]
    public async Task Manager_decision_fires_the_event_and_marks_the_card()
    {
        var id = await StartRequestedAsync();

        var res = await DecideAsync(id, "manager", by: "Ana");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (firedId, d) = Assert.Single(_factory.Gateway.Fires, f => f.OrderId == id);
        Assert.Equal(id, firedId);
        Assert.Equal(("manager", true, "Ana"), (d.Level, d.Approved, d.By));
        Assert.NotEqual(default, d.At);
        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/orders/{id}");
        Assert.Equal("Approved", view.GetProperty("manager").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Director_decision_after_the_escalation_fires_the_event()
    {
        var id = await StartRequestedAsync();
        await PostEventAsync(id, OrderEvent.Escalated(id, Levels.Director));

        var res = await DecideAsync(id, "director", approved: false, by: "Carla", reason: "no budget");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (_, d) = Assert.Single(_factory.Gateway.Fires, f => f.OrderId == id);
        Assert.Equal(("director", false, "no budget"), (d.Level, d.Approved, d.Reason));
    }

    [Fact]
    public async Task Deciding_without_a_name_falls_back_to_the_team_of_the_level()
    {
        var id = await StartRequestedAsync();

        await DecideAsync(id, "manager", by: " ");

        var (_, d) = Assert.Single(_factory.Gateway.Fires, f => f.OrderId == id);
        Assert.Equal("Manager team", d.By);
    }

    [Fact]
    public async Task Invalid_level_is_400_and_unknown_order_is_404()
    {
        var id = await StartRequestedAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(id, "rh")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync("nope", "manager")).StatusCode);
    }

    [Fact]
    public async Task The_manager_after_the_escalation_is_409_and_does_not_fire()
    {
        var id = await StartRequestedAsync();
        await PostEventAsync(id, OrderEvent.Escalated(id, Levels.Director));

        var res = await DecideAsync(id, "manager");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(_factory.Gateway.Fires.Where(f => f.OrderId == id));
    }

    [Fact]
    public async Task A_second_decision_is_409_and_does_not_fire_again()
    {
        var id = await StartRequestedAsync();
        await DecideAsync(id, "manager");

        var res = await DecideAsync(id, "manager", approved: false, reason: "mudei de ideia");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Single(_factory.Gateway.Fires, f => f.OrderId == id);
    }

    [Fact]
    public async Task Deciding_after_the_order_finished_is_409()
    {
        var id = await StartRequestedAsync();
        await PostEventAsync(id, OrderEvent.Failed(id, "boom"));

        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(id, "manager")).StatusCode);
    }

    [Fact]
    public async Task A_failing_fire_returns_502_and_leaves_the_level_pending_for_a_new_attempt()
    {
        var id = await StartRequestedAsync();
        _factory.Gateway.FailFireWith = new SkailGatewayException("skail answered 503");
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await DecideAsync(id, "manager")).StatusCode);
        }
        finally { _factory.Gateway.FailFireWith = null; }

        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/orders/{id}");
        Assert.Equal("Pending", view.GetProperty("manager").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(id, "manager")).StatusCode);
    }

    [Fact]
    public async Task Internal_events_update_the_state_served_by_the_api()
    {
        var id = await StartRequestedAsync();
        var d = new Decision(Levels.Manager, true, "Ana", null, DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.NoContent, (await PostEventAsync(id, OrderEvent.Approved(id, OrderResult.Approved(id, d)))).StatusCode);

        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/orders/{id}");
        Assert.Equal("Approved", view.GetProperty("phase").GetString());
        Assert.False(view.GetProperty("active").GetBoolean());
        Assert.StartsWith("PO-", view.GetProperty("result").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Stream_sends_the_snapshot_then_each_update_and_closes_when_the_order_ends()
    {
        var id = await StartIdAsync();
        using var response = await _http.GetAsync($"/api/orders/{id}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        await PostEventAsync(id, OrderEvent.Started(new PurchaseOrder(id, "a", "b", 1m)));
        await PostEventAsync(id, OrderEvent.ApprovalRequested(id, Levels.Manager));
        await PostEventAsync(id, OrderEvent.Failed(id, "boom"));

        var phases = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            phases.Add(JsonDocument.Parse(line[6..]).RootElement.GetProperty("phase").GetString()!);
        }

        Assert.Equal("Starting", phases[0]);
        Assert.Equal("Failed", phases[^1]);
        Assert.Contains("WaitingForManager", phases);
    }

    [Fact]
    public async Task Stream_of_an_unknown_order_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/orders/nope/stream")).StatusCode);
    }

    [Fact]
    public async Task Config_has_the_defaults_and_the_deadlines()
    {
        var config = await _http.GetFromJsonAsync<JsonElement>("/api/config");

        Assert.Equal(OrderLimits.DefaultSupplier, config.GetProperty("defaultSupplier").GetString());
        Assert.Equal(OrderLimits.DefaultDescription, config.GetProperty("defaultDescription").GetString());
        Assert.Equal(OrderLimits.DefaultAmount, config.GetProperty("defaultAmount").GetDecimal());
        Assert.Equal((7, 9), (config.GetProperty("managerDeadlineSeconds").GetInt32(), config.GetProperty("directorDeadlineSeconds").GetInt32()));
    }
}
