using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MathTournament;

public sealed class DeliveryDispatcher(Store store, HttpClient http, Func<string, string> address, string credential)
{
    // A failed recipient cannot block another recipient. Durable inboxes tolerate reordering.
    // Permanent 4xx responses are quarantined so they cannot block later events for this recipient.
    public async Task DispatchAsync(CancellationToken cancellationToken = default)
    {
        var paused = store.Read().PausedDestinations;
        var groups = store.Pending(DateTimeOffset.UtcNow)
            .Where(d => !paused.Contains(d.Destination))
            .GroupBy(d => d.Destination);

        await Task.WhenAll(groups.Select(async group =>
        {
            foreach (var delivery in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        address(delivery.Destination).TrimEnd('/') + "/internal/events");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                    request.Content = new StringContent(Json.Write(delivery.Event), Encoding.UTF8, "application/json");

                    using var response = await http.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        store.Delivered(delivery);
                        continue;
                    }

                    var status = (int)response.StatusCode;
                    var reason = $"HTTP {status} {response.ReasonPhrase}".Trim();

                    if (IsPermanentClientError(response.StatusCode))
                    {
                        store.MoveToDeadLetter(delivery, reason);

                        // Do not let a poison message block later events for this destination.
                        continue;
                    }

                    // 408, 429, 5xx and unexpected protocol responses are treated as transient.
                    store.Delivered(delivery, reason);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    store.Delivered(delivery, ex.Message);

                    // A transport failure may affect every later message to this recipient.
                    break;
                }
            }
        }));
    }

    private static bool IsPermanentClientError(HttpStatusCode status) =>
        (int)status is >= 400 and <= 499 && status is not HttpStatusCode.RequestTimeout
            && (int)status != 429;
}
