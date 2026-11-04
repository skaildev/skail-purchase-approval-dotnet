using System.Text.Json;
using System.Text.Json.Serialization;
using SkailPurchaseApproval.Contracts;
using SkailPurchaseApproval.Web.Orders;
using SkailPurchaseApproval.Web.Skail;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton(SkailOptions.FromEnvironment());
builder.Services.AddSingleton(ApprovalOptions.FromEnvironment());
builder.Services.AddHttpClient<ISkailGateway, SkailApiGateway>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/api/config", (ApprovalOptions approval) => Results.Ok(new
{
    defaultSupplier = OrderLimits.DefaultSupplier,
    defaultDescription = OrderLimits.DefaultDescription,
    defaultAmount = OrderLimits.DefaultAmount,
    managerDeadlineSeconds = approval.ManagerDeadlineSeconds,
    directorDeadlineSeconds = approval.DirectorDeadlineSeconds,
    monitorUrl = Environment.GetEnvironmentVariable("MONITOR_URL") is { Length: > 0 } url ? url : null,
}));

app.MapPost("/api/orders", async (StartOrderRequest? request, OrderStore store, ISkailGateway skail, ApprovalOptions approval, CancellationToken ct) =>
{
    var supplier = string.IsNullOrWhiteSpace(request?.Supplier) ? OrderLimits.DefaultSupplier : request.Supplier.Trim();
    var description = string.IsNullOrWhiteSpace(request?.Description) ? OrderLimits.DefaultDescription : request.Description.Trim();
    var amount = request?.Amount ?? OrderLimits.DefaultAmount;
    if (supplier.Length > OrderLimits.MaxTextLength || description.Length > OrderLimits.MaxTextLength)
        return Results.BadRequest(new { error = $"Supplier and description can have at most {OrderLimits.MaxTextLength} characters" });
    if (amount <= 0)
        return Results.BadRequest(new { error = "The amount must be greater than zero" });

    var purchase = new PurchaseOrder(Guid.NewGuid().ToString(), supplier, description, amount);
    var order = store.Create(purchase, approval.ManagerDeadlineSeconds, approval.DirectorDeadlineSeconds);
    try
    {
        await skail.TriggerAsync(purchase, approval.ManagerDeadlineSeconds, approval.DirectorDeadlineSeconds, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Remove(purchase.Id);
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(order);
});

app.MapGet("/api/orders/{id}", (string id, OrderStore store) =>
    store.Get(id) is { } order ? Results.Ok(order) : Results.NotFound());

// A decision: registered here (only the level that is waiting, first one wins) and fired to the function's WaitForEvent.
app.MapPost("/api/orders/{id}/decision", async (string id, DecisionRequest? request, OrderStore store, ISkailGateway skail, CancellationToken ct) =>
{
    if (!Levels.IsValid(request?.Level))
        return Results.BadRequest(new { error = $"Unknown level. Use '{Levels.Manager}' or '{Levels.Director}'" });

    var level = request!.Level!;
    var approved = request.Approved ?? true;
    var by = string.IsNullOrWhiteSpace(request.By) ? $"{Levels.Label(level)} team" : request.By.Trim();
    var reason = approved || string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
    var decision = new Decision(level, approved, by, reason, DateTimeOffset.UtcNow);

    switch (store.TryDecide(id, decision))
    {
        case DecisionOutcome.NotFound: return Results.NotFound();
        case DecisionOutcome.Duplicate: return Results.Conflict(new { error = "This order already has a decision" });
        case DecisionOutcome.WrongLevel: return Results.Conflict(new { error = $"The {Levels.Label(level)} is not the one waiting for this order" });
        case DecisionOutcome.Finished: return Results.Conflict(new { error = "The order is already finished" });
    }

    try
    {
        await skail.FireDecisionAsync(id, decision, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Release(id, level); // not delivered: the level can try again
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(store.Get(id));
});

// called by the worker's commands (ReportStatus, RequestApproval, ConfirmPurchase, RejectPurchase, CancelForNoResponse)
app.MapPost("/internal/orders/{id}/events", (string id, OrderEvent evt, OrderStore store) =>
{
    store.Apply(evt with { OrderId = id });
    return Results.NoContent();
});

app.MapGet("/api/orders/{id}/stream", async (string id, HttpContext http, OrderStore store, CancellationToken ct) =>
{
    using var subscription = store.Subscribe(id); // subscribe before reading the snapshot: no update is lost
    if (store.Get(id) is not { } current) return Results.NotFound();

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(OrderState state)
    {
        var json = JsonSerializer.Serialize(state, Json.Options);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await Send(current);
        if (current.IsTerminal) return Results.Empty;

        await foreach (var state in subscription.Reader.ReadAllAsync(ct))
        {
            await Send(state);
            if (state.IsTerminal) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    return Results.Empty;
});

app.Run();

public sealed record StartOrderRequest(string? Supplier, string? Description, decimal? Amount);

public sealed record DecisionRequest(string? Level, bool? Approved, string? By, string? Reason);

public partial class Program;
