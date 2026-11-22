using MathTournament;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
var service = builder.Configuration["SERVICE"] ?? "tournament";
Require.That(new[] { "tournament", "scores", "results" }.Contains(service), "invalid_service");
var store = new Store(builder.Configuration["DB"] ?? $"data/{service}.db", service);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(new ServiceName(service));
builder.Services.AddHttpClient("ui", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<DeliveryWorker>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (RuleException e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { code = e.Message }); }
    catch (JsonException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_json" }); }
});
string Actor(HttpRequest r, string role)
{
    var token = r.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal);
    var actors = builder.Configuration["ACTORS"] ?? "organizer:organizer:organizer-demo;captain-a:captain:captain-a-demo;captain-b:captain:captain-b-demo";
    var actor = actors.Split(';').Select(x => x.Split(':')).FirstOrDefault(x => x.Length == 3 && Secure(x[2], token));
    Require.That(actor != null && actor[1] == role, "authentication_or_role_required", 403);
    return actor![0];
}
string Key(string producer) => builder.Configuration["KEY_" + producer.ToUpperInvariant()] ?? producer + "-integration-demo";
app.MapGet("/health", () => Results.Ok(new { service, status = "ok" }));
app.MapMethods("/ui-api/tournament/{**path}", ["GET", "POST", "PUT", "DELETE"], async (string? path, HttpContext context, IHttpClientFactory clients) =>
{
    var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
    Require.That(segments.Length > 0 && segments[0] == "tournaments" && !segments.Any(x => x is "." or ".." || x.Contains('\\')), "invalid_ui_route", 400);
    var peer = builder.Configuration["PEER_TOURNAMENT"] ?? "http://localhost:5101";
    using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method), peer.TrimEnd('/') + "/" + string.Join('/', segments.Select(Uri.EscapeDataString)));
    foreach (var header in new[] { "Authorization", "If-Match", "Idempotency-Key" })
        if (context.Request.Headers.TryGetValue(header, out var value)) outgoing.Headers.TryAddWithoutValidation(header, value.ToArray());
    if (context.Request.Method is "POST" or "PUT")
    {
        using var reader = new StreamReader(context.Request.Body);
        outgoing.Content = new StringContent(await reader.ReadToEndAsync(context.RequestAborted), Encoding.UTF8, "application/json");
    }
    try
    {
        using var response = await clients.CreateClient("ui").SendAsync(outgoing, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    { context.Response.StatusCode = 502; await context.Response.WriteAsJsonAsync(new { code = "tournament_service_unavailable" }); }
});
app.MapPost("/ui-api/fixtures/{id}/{game}", async (string id, string game, HttpRequest request, IHttpClientFactory clients) =>
{
    Actor(request, "organizer");
    var peer = builder.Configuration["PEER_SIMULATOR"] ?? "http://localhost:5104";
    using var outgoing = new HttpRequestMessage(HttpMethod.Post, peer.TrimEnd('/') + "/fixtures/" + Uri.EscapeDataString(id) + "/" + Uri.EscapeDataString(game));
    outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", builder.Configuration["SIMULATOR_KEY"] ?? "simulator-demo");
    try
    {
        using var response = await clients.CreateClient("ui").SendAsync(outgoing, request.HttpContext.RequestAborted);
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    { return Results.Json(new { code = "simulator_unavailable" }, statusCode: 502); }
});
app.MapGet("/openapi.json", () => Results.Text(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "openapi.json")), "application/json"));
app.MapPost("/internal/events", (Event e, HttpRequest r) =>
{
    Require.That(e.Producer is "tournament" or "scores" or "results" or "team" or "checking", "invalid_producer", 403);
    Require.That(Secure(Key(e.Producer), r.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal)), "invalid_producer_credential", 403);
    store.Update((s, engine) => engine.Receive(s, e)); return Results.Accepted();
});
app.MapGet("/internal/diagnostics", (HttpRequest r) =>
{
    Actor(r, "organizer"); var s = store.Read();
    return Results.Ok(new { pending = s.Pending.Keys, s.Quarantine, outboxPending = store.Pending().Count });
});
if (service == "tournament")
{
    app.MapGet("/tournaments", (HttpRequest request) =>
    {
        string? organizer = null;
        if (!string.IsNullOrWhiteSpace(request.Headers.Authorization))
        { try { organizer = Actor(request, "organizer"); } catch (RuleException) { } }
        return Results.Ok(store.Read().Tournaments.Values.Where(t => t.Status != "Draft" || t.Organizer == organizer)
            .OrderByDescending(t => t.Starts).Select(t => new { t.Id, t.Name, t.Status, t.Starts, t.Ends,
                Teams = t.Teams.Count, Games = t.Games.Count }));
    });
    app.MapPost("/tournaments", (Tournament t, HttpRequest r) =>
    {
        var actor = Actor(r, "organizer");
        var id = CommandId(r);
        return Results.Text(store.Update((s, engine) => engine.Create(s, t, actor), id, actor + Json.Write(t)), "application/json");
    });
    app.MapGet("/tournaments/{id}", (string id, HttpRequest r) =>
    {
        var s = store.Read(); Require.That(s.Tournaments.TryGetValue(id, out var t), "tournament_not_found", 404);
        var organizer = r.Headers.Authorization.ToString() != "" && TryOrganizer(r, t!.Organizer);
        Require.That(t!.Status != "Draft" || organizer, "not_published", 404);
        if (organizer) return Results.Ok(t);
        return Results.Ok(new { t.Id, t.Name, t.Status, t.Revision, t.Starts, t.Ends, t.TeamSize, t.Regulation, t.TiePolicy,
            Games = t.Games.Select(g => new { g.Config.Id, g.Config.Type, g.Status, g.SolutionsDue, g.AppealsDue, g.SolutionsClosed, g.AppealsClosed }),
            Teams = t.Teams.Select(x => new { x.Id, x.Name }) });
    });
    bool TryOrganizer(HttpRequest r, string owner) { try { return Actor(r, "organizer") == owner; } catch (RuleException) { return false; } }
    app.MapPut("/tournaments/{id}/games/{game}", (string id, string game, GameConfig config, HttpRequest r) =>
    {
        Require.That(config.Id == game, "game_id_mismatch", 400);
        return Execute(id, "configure", r, config: config, game: game);
    });
    app.MapDelete("/tournaments/{id}/games/{game}", (string id, string game, HttpRequest r) => Execute(id, "delete", r, game: game));
    app.MapPost("/tournaments/{id}/teams", (string id, Team team, HttpRequest r) => Execute(id, "register", r, team: team));
    app.MapPost("/tournaments/{id}/publish", (string id, HttpRequest r) => Execute(id, "publish", r));
    app.MapPost("/tournaments/{id}/start", (string id, HttpRequest r) => Execute(id, "start", r));
    app.MapPost("/tournaments/{id}/games/{game}/start", (string id, string game, HttpRequest r) => Execute(id, "start-game", r, game: game));
    IResult Execute(string id, string action, HttpRequest r, GameConfig? config = null, Team? team = null, string? game = null)
    {
        var actor = Actor(r, action == "register" ? "captain" : "organizer");
        Require.That(long.TryParse(r.Headers.IfMatch.ToString().Trim('"'), out var revision), "if_match_revision_required", 428);
        var commandId = CommandId(r);
        return Results.Text(store.Update((s, engine) => engine.Command(s, id, action, actor, revision, config, team, game),
            commandId, Json.Write(new { actor, id, action, revision, config, team, game })), "application/json");
    }
}
if (service is "scores" or "results")
{
    app.MapGet("/tournaments/{id}/table", (string id) => Table(store.Read(), id));
    app.MapGet("/tournaments/{id}/stream", async (string id, HttpContext context) =>
    {
        context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-cache";
        string? previous = null;
        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var current = Json.Write(Table(store.Read(), id));
                if (current != previous) { await context.Response.WriteAsync("data: " + current + "\n\n", context.RequestAborted); previous = current; }
                else await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(500, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    });
}
app.Run();

static string CommandId(HttpRequest r)
{
    var id = r.Headers["Idempotency-Key"].ToString(); Require.That(!string.IsNullOrWhiteSpace(id), "idempotency_key_required", 400); return id;
}
static bool Secure(string expected, string actual) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
static object Table(State s, string id)
{
    if (!s.Tournaments.TryGetValue(id, out var t)) return new { status = "WaitingForTournament", rows = Array.Empty<object>() };
    var ranked = Engine.Rank(t.Teams.Select(team => (team.Id, (decimal)t.Games.Sum(g => s.Finals.GetValueOrDefault(State.Key(id, g.Config.Id))?.SingleOrDefault(x => x.TeamId == team.Id)?.Points ?? 0))));
    return new { t.Id, complete = s.Winners.Contains(id), games = t.Games.Select(g => new { g.Config.Id, tasks = g.Config.Tasks.Select(x => x.Id) }),
        rows = ranked.Select(rank => new { id = rank.TeamId, name = t.Teams.Single(x => x.Id == rank.TeamId).Name, rank.Place, total = rank.Score,
            games = t.Games.Select(g => new { id = g.Config.Id,
                score = s.Scores.GetValueOrDefault(State.Key(id, g.Config.Id))?.Cells[rank.TeamId].Values.Sum(x => x.Points),
                place = s.Scores.TryGetValue(State.Key(id, g.Config.Id), out var live)
                    ? 1 + live.Cells.Values.Count(cells => cells.Values.Sum(x => x.Points) > live.Cells[rank.TeamId].Values.Sum(x => x.Points)) : (int?)null,
                cells = s.Scores.GetValueOrDefault(State.Key(id, g.Config.Id))?.Cells[rank.TeamId]
                    .ToDictionary(x => x.Key, x => new { x.Value.Status, x.Value.Points }),
                final = s.Finals.GetValueOrDefault(State.Key(id, g.Config.Id))?.SingleOrDefault(x => x.TeamId == rank.TeamId) }) }) };
}
record ServiceName(string Value);
sealed class DeliveryWorker(Store store, ServiceName service, IConfiguration config, ILogger<DeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                store.Update((s, engine) => engine.Tick(s, DateTimeOffset.UtcNow));
                foreach (var d in store.Pending())
                {
                    try
                    {
                        var defaultPort = d.Destination switch { "tournament" => 5101, "scores" => 5102, "results" => 5103, _ => 5104 };
                        var url = config["PEER_" + d.Destination.ToUpperInvariant()] ?? $"http://localhost:{defaultPort}";
                        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/internal/events");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["KEY_" + service.Value.ToUpperInvariant()] ?? service.Value + "-integration-demo");
                        request.Content = new StringContent(Json.Write(d.Event), Encoding.UTF8, "application/json");
                        using var response = await http.SendAsync(request, stoppingToken); response.EnsureSuccessStatusCode(); store.Delivered(d);
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { store.Delivered(d, e.Message); }
                }
            }
            catch (Exception e) { log.LogError(e, "Background iteration failed"); }
            await Task.Delay(250, stoppingToken);
        }
    }
}
