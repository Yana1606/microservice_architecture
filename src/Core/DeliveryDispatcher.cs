using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MathTournament;

public sealed class DeliveryDispatcher
{
    private readonly IStateStore stateStore;
    private readonly IOutboxStore outboxStore;
    private readonly HttpClient http;
    private readonly Func<string, string> address;
    private readonly string credential;

    public DeliveryDispatcher(
        IStateStore stateStore,
        IOutboxStore outboxStore,
        HttpClient http,
        Func<string, string> address,
        string credential)
    {
        this.stateStore = stateStore
            ?? throw new ArgumentNullException(nameof(stateStore));

        this.outboxStore = outboxStore
            ?? throw new ArgumentNullException(nameof(outboxStore));

        this.http = http
            ?? throw new ArgumentNullException(nameof(http));

        this.address = address
            ?? throw new ArgumentNullException(nameof(address));

        this.credential = credential
            ?? throw new ArgumentNullException(nameof(credential));
    }

    // Compatibility constructor for existing callers.
    public DeliveryDispatcher(
        Store store,
        HttpClient http,
        Func<string, string> address,
        string credential)
        : this(store, store, http, address, credential)
    {
    }

    public async Task DispatchAsync(
        CancellationToken cancellationToken = default)
    {
        var pausedDestinations =
            stateStore.Read().PausedDestinations;

        var deliveries = outboxStore.Pending(
            DateTimeOffset.UtcNow);

        var groups = deliveries
            .Where(delivery =>
                !pausedDestinations.Contains(delivery.Destination))
            .GroupBy(delivery => delivery.Destination);

        await Task.WhenAll(
            groups.Select(async group =>
            {
                foreach (var delivery in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var destination =
                            address(delivery.Destination).TrimEnd('/');

                        using var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            destination + "/internal/events");

                        request.Headers.Authorization =
                            new AuthenticationHeaderValue(
                                "Bearer",
                                credential);

                        request.Content = new StringContent(
                            Json.Write(delivery.Event),
                            Encoding.UTF8,
                            "application/json");

                        using var response = await http.SendAsync(
                            request,
                            cancellationToken);

                        if (response.IsSuccessStatusCode)
                        {
                            outboxStore.Delivered(delivery);
                            continue;
                        }

                        var status = (int)response.StatusCode;

                        var reason =
                            $"HTTP {status} {response.ReasonPhrase}".Trim();

                        if (IsPermanentClientError(response.StatusCode))
                        {
                            outboxStore.MoveToDeadLetter(
                                delivery,
                                reason);

                            // Continue with the next event.
                            continue;
                        }

                        // Temporary HTTP errors are retried later.
                        outboxStore.Delivered(
                            delivery,
                            reason);

                        // Preserve ordering for this destination.
                        break;
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (HttpRequestException exception)
                    {
                        outboxStore.Delivered(
                            delivery,
                            exception.Message);

                        break;
                    }
                    catch (TaskCanceledException exception)
                    {
                        outboxStore.Delivered(
                            delivery,
                            exception.Message);

                        break;
                    }
                }
            }));
    }

    private static bool IsPermanentClientError(
        HttpStatusCode status)
    {
        var code = (int)status;

        return code >= 400 &&
               code <= 499 &&
               status != HttpStatusCode.RequestTimeout &&
               code != 429;
    }
}