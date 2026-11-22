using MathTournament;
using System.Net.Http.Headers;
using System.Text;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try { using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};using var response=await client.GetAsync("http://127.0.0.1:8080/health");Environment.ExitCode=response.IsSuccessStatusCode?0:1; }
    catch(HttpRequestException){Environment.ExitCode=1;}
    catch(TaskCanceledException){Environment.ExitCode=1;}
    return;
}
// Test fixture only: does not implement team workflows or jury assignment.
var builder = WebApplication.CreateBuilder(args);
var path = builder.Configuration["DB"] ?? "data/simulator.db";
var store = new Store(path, "simulator");
var app = builder.Build();
app.Use(async (context,next) =>
{
    try { await next(context); }
    catch (RuleException ex) { context.Response.StatusCode=ex.Status;await context.Response.WriteAsJsonAsync(new{code=ex.Message}); }
    catch (System.Text.Json.JsonException) { context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{code="invalid_json"}); }
});
app.MapGet("/health", () => new { status = "ok", mode = "test-simulator" });
app.MapPost("/internal/events", (Event e, HttpRequest r) =>
{
    Validation.Envelope(e);
    var expected = builder.Configuration["KEY_" + e.Producer.ToUpperInvariant()] ?? e.Producer + "-integration-demo";
    if (r.Headers.Authorization != "Bearer " + expected) return Results.Unauthorized();
    if (e.Type is "TournamentStarted" or "GameStarted") Validation.Event(e);
    store.Update((s, _) =>
    {
        var json = Json.Write(e);
        if (s.Receipts.TryGetValue(e.Id, out var old)) Require.That(old == json, "event_id_payload_conflict");
        else s.Receipts[e.Id] = json;
    });
    return Results.Accepted();
});
app.MapPost("/fixtures/{tournament}/{game}", async (string tournament, string game, HttpRequest r) =>
{
    if (r.Headers.Authorization != "Bearer " + (builder.Configuration["SIMULATOR_KEY"] ?? "simulator-demo")) return Results.Unauthorized();
    var receiptKey = "fixture/" + tournament + "/" + game;
    // Persist the full immutable fixture before sending; retry this endpoint safely after any interruption.
    store.Update((s, _) =>
    {
        if (s.Receipts.ContainsKey(receiptKey)) return;
        var started = s.Receipts.Values.Where(x => x.StartsWith('{')).Select(Json.Read<Event>).LastOrDefault(e => e.Type == "TournamentStarted" && e.TournamentId == tournament);
        var gameStarted = s.Receipts.Values.Where(x => x.StartsWith('{')).Select(Json.Read<Event>).Any(e => e.Type == "GameStarted" && e.TournamentId == tournament && e.GameId == game);
        Require.That(started != null && gameStarted, "wait_for_game_started");
        var t = started!.Body<Tournament>(); var g = t.Games.Single(x => x.Config.Id == game);
        var events = new List<Event>(); var anonymous = new List<string>(); var verdictIds = new List<string>();
        for (var i = 0; i < t.Teams.Count; i++)
        for (var j = 0; j < g.Config.Tasks.Count; j++)
        {
            var anon = Guid.NewGuid().ToString("N"); anonymous.Add(anon);
            events.Add(Event.Create("SolutionBound", "team", tournament, game, new Binding(anon, t.Teams[i].Id, g.Config.Tasks[j].Id, "normal")));
            var initial = Event.Create("VerdictRecorded", "checking", tournament, game,
                new Verdict(anon, 1, i == 0 && j == 0 ? "Incorrect" : i == 0 ? "Correct" : "Incorrect", t.Jury[0], "Jury"));
            events.Add(initial); verdictIds.Add(initial.Id);
            if (j == 0)
            {
                var appeal = Event.Create("VerdictRecorded", "checking", tournament, game,
                    new Verdict(anon, 2, i == 0 ? "AppealUpheld" : "AppealRejected", t.Senior, "Senior"));
                events.Add(appeal); verdictIds.Add(appeal.Id);
            }
        }
        events.Add(Event.Create("TeamSealed", "team", tournament, game, new TeamSeal(anonymous)));
        events.Add(Event.Create("ChecksSealed", "checking", tournament, game, new CheckSeal(verdictIds)));
        // Reverse delivery intentionally: seals and appeals arrive ahead of bindings and initial verdicts.
        events.Reverse(); s.Receipts[receiptKey] = Json.Write(events);
    });
    var fixture = Json.Read<List<Event>>(store.Read().Receipts[receiptKey]);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    foreach (var e in fixture)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, (builder.Configuration["SCORES_URL"] ?? "http://localhost:5102") + "/internal/events");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", builder.Configuration["KEY_" + e.Producer.ToUpperInvariant()] ?? e.Producer + "-integration-demo");
        message.Content = new StringContent(Json.Write(e), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message); response.EnsureSuccessStatusCode();
    }
    return Results.Ok(new { sent = fixture.Count, replaySafe = true });
});
app.Run();
