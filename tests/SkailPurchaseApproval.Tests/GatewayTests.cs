using System.Net;
using System.Text.Json;
using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Web.Skail;

namespace SkailPurchaseApproval.Tests;

public class GatewayTests
{
    private static readonly SkailOptions Opts = new()
    {
        BaseUrl = "https://skail.example.test/",
        Namespace = "tenant-1/env-1",
        Key = "env-key",
        Workload = "purchase-approval:v0.0.1",
    };

    private static readonly PurchaseOrder Order = new("o-1", "Acme", "10 notebooks", 25000.5m);
    private static readonly Decision Dec = new(Levels.Manager, true, "Ana", null, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private static SkailApiGateway Gateway(StubHandler handler, SkailOptions? options = null) =>
        new(new HttpClient(handler), options ?? Opts, retryDelay: _ => TimeSpan.Zero);

    [Fact]
    public async Task Trigger_posts_the_order_and_the_deadlines_to_the_function_route_of_the_workload()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync(Order, 60, 90);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://skail.example.test/api/v1/trigger/purchase-approval/v0.0.1/ApprovePurchase/o-1", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("Bearer env-key", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");

        var args = JsonDocument.Parse(body).RootElement;
        Assert.Equal(3, args.GetArrayLength());
        Assert.Equal(("o-1", "Acme", "10 notebooks", 25000.5m),
            (args[0].GetProperty("Id").GetString(), args[0].GetProperty("Supplier").GetString(),
             args[0].GetProperty("Description").GetString(), args[0].GetProperty("Amount").GetDecimal()));
        Assert.Equal((60, 90), (args[1].GetInt32(), args[2].GetInt32()));
    }

    [Fact]
    public async Task Fire_goes_to_the_decision_event_with_the_order_id_as_instance_id()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).FireDecisionAsync("o-1", Dec);

        var (request, _) = Assert.Single(handler.Calls);
        Assert.Equal("https://skail.example.test/api/v1/fire/PURCHASE_DECISION/o-1", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
    }

    [Fact]
    public async Task Fire_body_is_the_decision_with_the_property_names_of_the_declared_type()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).FireDecisionAsync("o-1", Dec with { Approved = false, Reason = "too expensive", Level = Levels.Director });

        var body = JsonDocument.Parse(handler.Calls[0].Body).RootElement;
        Assert.False(body.GetProperty("Approved").GetBoolean());
        Assert.Equal("Ana", body.GetProperty("By").GetString());
        Assert.Equal("too expensive", body.GetProperty("Reason").GetString());
        Assert.Equal("director", body.GetProperty("Level").GetString());
        Assert.True(body.TryGetProperty("At", out _));
    }

    [Fact]
    public async Task Fire_of_an_unknown_level_is_rejected_before_calling_skail()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).FireDecisionAsync("o-1", Dec with { Level = "rh" }));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Options_read_the_api_base_url_from_SKAIL_BASE_URL()
    {
        var names = new[] { "SKAIL_BASE_URL", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_WORKLOAD" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("SKAIL_BASE_URL", "https://env.example.test");
            Environment.SetEnvironmentVariable("SKAIL_NAMESPACE", "t/e");
            Environment.SetEnvironmentVariable("SKAIL_KEY", "k");
            Environment.SetEnvironmentVariable("SKAIL_WORKLOAD", "w:v1");

            var options = SkailOptions.FromEnvironment();

            Assert.Equal("https://env.example.test", options.BaseUrl);
            Assert.Equal(("t/e", "k", "w:v1"), (options.Namespace, options.Key, options.Workload));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("abc", 60)]
    [InlineData("0", 60)]
    [InlineData("-5", 60)]
    [InlineData("172800", 172800)]
    public void Deadlines_come_from_the_environment_and_fall_back_to_the_demo_default(string? value, int expected)
    {
        const string manager = "APPROVAL_MANAGER_DEADLINE_SECONDS", director = "APPROVAL_DIRECTOR_DEADLINE_SECONDS";
        var saved = new[] { manager, director }.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(manager, value);
            Environment.SetEnvironmentVariable(director, value);

            var options = ApprovalOptions.FromEnvironment();

            Assert.Equal((expected, expected), (options.ManagerDeadlineSeconds, options.DirectorDeadlineSeconds));
        }
        finally
        {
            foreach (var (name, v) in saved) Environment.SetEnvironmentVariable(name, v);
        }
    }

    [Fact]
    public async Task Order_id_is_escaped_in_the_routes()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync(Order with { Id = "a/b c" }, 1, 1);
        await Gateway(handler).FireDecisionAsync("a/b c", Dec);

        Assert.EndsWith("/ApprovePurchase/a%2Fb%20c", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        Assert.EndsWith("/PURCHASE_DECISION/a%2Fb%20c", handler.Calls[1].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_the_same_id_and_body()
    {
        var handler = new StubHandler((_, n) => new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        await Gateway(handler).FireDecisionAsync("o-1", Dec);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Single(handler.Calls.Select(c => c.Request.RequestUri!.ToString()).Distinct());
        Assert.Single(handler.Calls.Select(c => c.Body).Distinct());
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        var handler = StubHandler.Status(HttpStatusCode.TooManyRequests);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync(Order, 1, 1));

        Assert.Equal(SkailApiGateway.MaxAttempts, handler.Calls.Count);
        Assert.Contains("giving up", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthorized_is_not_retried_and_points_at_the_key_and_namespace(HttpStatusCode status)
    {
        var handler = StubHandler.Status(status);

        var e = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).FireDecisionAsync("o", Dec));

        Assert.Single(handler.Calls);
        Assert.Contains("SKAIL_KEY", e.Message);
    }

    [Fact]
    public async Task Missing_base_url_or_a_malformed_workload_is_reported()
    {
        var noUrl = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { Workload = "w:v1" }).TriggerAsync(Order, 1, 1));
        Assert.Contains("SKAIL_BASE_URL", noUrl.Message);

        var badWorkload = await Assert.ThrowsAsync<SkailGatewayException>(() =>
            Gateway(StubHandler.Status(HttpStatusCode.OK), new SkailOptions { BaseUrl = "https://x", Workload = "oops" }).TriggerAsync(Order, 1, 1));
        Assert.Contains("SKAIL_WORKLOAD", badWorkload.Message);
    }
}
