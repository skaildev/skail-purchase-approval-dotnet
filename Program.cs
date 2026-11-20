using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailPurchaseApproval;
using SkailPurchaseApproval.Demo;
using SkailPurchaseApproval.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run [-- <order id>]
//   then open http://localhost:8080; an order id follows an order that already exists (to see it resume)
var argument = args.FirstOrDefault(a => !a.StartsWith('-'));
if (argument is not null && !Guid.TryParse(argument, out _))
{
    Console.Error.WriteLine($"Unknown argument '{argument}'. Pass an order id, or nothing.");
    return 1;
}

DotEnv.Load();
string[] required = ["SKAIL_WORKLOAD", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_SIDECAR"];
var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Missing {string.Join(", ", missing)}. Copy .env.example to .env and fill it in.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

// keep the console for the demo: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// the page: localhost only, unless DEMO_LISTEN says otherwise (the container listens on all interfaces)
var listen = Environment.GetEnvironmentVariable("DEMO_LISTEN") ?? "http://localhost:8080";
builder.WebHost.UseUrls(listen);

builder.Services.AddSingleton<Timeline>();
builder.Services.AddSingleton<PurchaseDesk>();
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));

// hosts the worker that executes PurchaseFunctions; reads the SKAIL_* variables
builder.UseSkail();

var app = builder.Build();
var desk = app.Services.GetRequiredService<PurchaseDesk>();
if (argument is not null) desk.Follow(Guid.Parse(argument).ToString());

var page = Path.Combine(AppContext.BaseDirectory, "Page", "index.html");
app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));
app.MapGet("/api/state", () => Results.Json(desk.View()));
app.MapPost("/api/orders", async (CancellationToken ct) => Results.Json(new { id = await desk.PlaceAsync(ct) }));
app.MapPost("/api/decisions/{level}/{approved:bool}", async (string level, bool approved, CancellationToken ct) =>
    await desk.DecideAsync(level, approved, ct) ? Results.Ok() : Results.Conflict());

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Open http://localhost:8080 to place a purchase order and play the manager and the director. Ctrl+C exits."));

await app.RunAsync();
return 0;
