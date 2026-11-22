using MathTournament;
sealed class SimulatorDelivery(Store store,IConfiguration config,ILogger<SimulatorDelivery> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Yield();using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};
        var dispatcher=new DeliveryDispatcher(store,http,d=>config["PEER_"+d.ToUpperInvariant()]??"http://localhost:5101",config["KEY_SIMULATOR"]??"simulator-integration-demo");
        while(!token.IsCancellationRequested)
        {
            try{await dispatcher.DispatchAsync(token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception ex){log.LogError(ex,"Simulator delivery retained for retry");}
            await Task.Delay(250,token);
        }
    }
}
sealed class SimulatorRecovery(Store store):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await Task.Yield();using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do{store.Update((s,e)=>e.Tick(s,DateTimeOffset.UtcNow));}while(await timer.WaitForNextTickAsync(token));
    }
}
