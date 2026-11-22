using System.Net.Http.Headers;
using System.Text;
namespace MathTournament;

public sealed class DeliveryDispatcher(Store store, HttpClient http, Func<string, string> address, string credential)
{
    // A failed recipient cannot block another recipient. Durable inboxes tolerate reordering.
    public async Task DispatchAsync(CancellationToken cancellationToken = default)
    {
        var paused=store.Read().PausedDestinations;
        var groups = store.Pending(DateTimeOffset.UtcNow).Where(d=>!paused.Contains(d.Destination)).GroupBy(d => d.Destination);
        await Task.WhenAll(groups.Select(async group =>
        {
            foreach (var delivery in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, address(delivery.Destination).TrimEnd('/') + "/internal/events");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                    request.Content = new StringContent(Json.Write(delivery.Event), Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode(); store.Delivered(delivery);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    store.Delivered(delivery, ex.Message);
                    break; // Back off persistently and let other recipients continue.
                }
            }
        }));
    }
}
