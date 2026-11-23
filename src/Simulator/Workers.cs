using MathTournament;

sealed class SimulatorDelivery(
    IStateStore stateStore,
    IOutboxStore outboxStore,
    IConfiguration config,
    ILogger<SimulatorDelivery> log) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken token)
    {
        await Task.Yield();

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        var dispatcher = new DeliveryDispatcher(
            stateStore,
            outboxStore,
            http,
            destination =>
                config[
                    "PEER_" + destination.ToUpperInvariant()]
                ?? "http://localhost:5101",
            config["KEY_SIMULATOR"]
                ?? "simulator-integration-demo");

        while (!token.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DispatchAsync(token);
            }
            catch (OperationCanceledException)
                when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log.LogError(
                    exception,
                    "Simulator delivery retained for retry");
            }

            await Task.Delay(250, token);
        }
    }
}

sealed class SimulatorRecovery(
    IStateStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken token)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(250));

        do
        {
            store.Update(
                (state, engine) =>
                    engine.Tick(state, DateTimeOffset.UtcNow));
        }
        while (await timer.WaitForNextTickAsync(token));
    }
}