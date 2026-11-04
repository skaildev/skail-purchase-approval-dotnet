using System.Net.Http.Json;
using SkailPurchaseApproval.Contracts;

namespace SkailPurchaseApproval.Worker.Progress;

/// <summary>Reports progress to the web app: POST {web}/internal/orders/{id}/events.</summary>
public sealed class HttpProgressNotifier(HttpClient http) : IProgressNotifier
{
    public async Task NotifyAsync(OrderEvent evt, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"internal/orders/{Uri.EscapeDataString(evt.OrderId)}/events", evt, Json.Options, ct);
        // when a command fails, this message is what the skail Monitor shows
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Web app answered {(int)response.StatusCode} to the progress event", null, response.StatusCode);
    }
}
