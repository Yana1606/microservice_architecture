using MathTournament;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        using var response = await client.GetAsync(
            "http://127.0.0.1:8080/health");

        Environment.ExitCode =
            response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        Environment.ExitCode = 1;
    }
    catch (TaskCanceledException)
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(
    new WebApplicationOptions
    {
        Args = args,
        WebRootPath = Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot")
    });

var service = builder.Configuration["SERVICE"] ?? "tournament";

Require.That(
    new[] { "tournament", "scores", "results" }.Contains(service),
    "invalid_service");

var store = new Store(
    builder.Configuration["DB"] ?? $"data/{service}.db",
    service);

// Register the implementation and its narrow interfaces.
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IStateStore>(store);
builder.Services.AddSingleton<IOutboxStore>(store);
builder.Services.AddSingleton<IPublicSnapshotStore>(store);

builder.Services.AddSingleton(new ServiceName(service));

builder.Services.AddHttpClient(
    "ui",
    client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.Configure<RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = true);

builder.Services.AddMemoryCache(
    options => options.SizeLimit = 2048);

builder.Services.AddSingleton<TableSnapshotCache>();

builder.Services.AddHostedService<DeadlineWorker>();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RuleException exception)
    {
        context.Response.StatusCode = exception.Status;

        await context.Response.WriteAsJsonAsync(
            new { code = exception.Message });
    }
    catch (JsonException)
    {
        context.Response.StatusCode = 400;

        await context.Response.WriteAsJsonAsync(
            new { code = "invalid_json" });
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = 400;

        await context.Response.WriteAsJsonAsync(
            new { code = "invalid_request" });
    }
});

string Actor(HttpRequest request, string role)
{
    var token = request.Headers.Authorization
        .ToString()
        .Replace("Bearer ", "", StringComparison.Ordinal);

    var actors = builder.Configuration["ACTORS"] ??
        "organizer:organizer:organizer-demo;" +
        "captain-a:captain:captain-a-demo;" +
        "captain-b:captain:captain-b-demo";

    var actor = actors
        .Split(';')
        .Select(x => x.Split(':'))
        .FirstOrDefault(x =>
            x.Length == 3 && Secure(x[2], token));

    Require.That(
        actor != null && actor[1] == role,
        "authentication_or_role_required",
        403);

    return actor![0];
}

string Key(string producer) =>
    builder.Configuration[
        "KEY_" + producer.ToUpperInvariant()]
    ?? producer + "-integration-demo";

app.MapGet(
    "/health",
    () => Results.Ok(new { service, status = "ok" }));

app.MapMethods(
    "/ui-api/tournament/{**path}",
    ["GET", "POST", "PUT", "DELETE"],
    async (
        string? path,
        HttpContext context,
        IHttpClientFactory clients) =>
    {
        var segments = (path ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        Require.That(
            segments.Length > 0 &&
            segments[0] == "tournaments" &&
            !segments.Any(x =>
                x is "." or ".." || x.Contains('\\')),
            "invalid_ui_route",
            400);

        var peer =
            builder.Configuration["PEER_TOURNAMENT"]
            ?? "http://localhost:5101";

        using var outgoing = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            peer.TrimEnd('/') + "/" +
            string.Join(
                '/',
                segments.Select(Uri.EscapeDataString)));

        foreach (var header in new[]
        {
            "Authorization",
            "If-Match",
            "Idempotency-Key"
        })
        {
            if (context.Request.Headers.TryGetValue(
                header,
                out var value))
            {
                outgoing.Headers.TryAddWithoutValidation(
                    header,
                    value.ToArray());
            }
        }

        if (context.Request.Method is "POST" or "PUT")
        {
            using var reader = new StreamReader(
                context.Request.Body);

            outgoing.Content = new StringContent(
                await reader.ReadToEndAsync(
                    context.RequestAborted),
                Encoding.UTF8,
                "application/json");
        }

        try
        {
            using var response = await clients
                .CreateClient("ui")
                .SendAsync(
                    outgoing,
                    context.RequestAborted);

            context.Response.StatusCode =
                (int)response.StatusCode;

            context.Response.ContentType =
                response.Content.Headers.ContentType?.ToString()
                ?? "application/json";

            await response.Content.CopyToAsync(
                context.Response.Body,
                context.RequestAborted);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException)
        {
            context.Response.StatusCode = 502;

            await context.Response.WriteAsJsonAsync(
                new
                {
                    code = "tournament_service_unavailable"
                });
        }
    });

app.MapPost(
    "/ui-api/fixtures/{id}/{game}",
    async (
        string id,
        string game,
        HttpRequest request,
        IHttpClientFactory clients) =>
    {
        Actor(request, "organizer");

        var peer =
            builder.Configuration["PEER_SIMULATOR"]
            ?? "http://localhost:5104";

        using var outgoing = new HttpRequestMessage(
            HttpMethod.Post,
            peer.TrimEnd('/') +
            "/fixtures/" +
            Uri.EscapeDataString(id) +
            "/" +
            Uri.EscapeDataString(game));

        outgoing.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                builder.Configuration["SIMULATOR_KEY"]
                ?? "simulator-demo");

        try
        {
            using var response = await clients
                .CreateClient("ui")
                .SendAsync(
                    outgoing,
                    request.HttpContext.RequestAborted);

            return Results.Content(
                await response.Content.ReadAsStringAsync(),
                "application/json",
                statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException)
        {
            return Results.Json(
                new { code = "simulator_unavailable" },
                statusCode: 502);
        }
    });

app.MapGet(
    "/lab/status",
    () => new
    {
        enabled = builder.Configuration["ENABLE_LAB_DEMOS"] == "1"
    });

app.MapPost(
    "/lab/delivery/{destination}/{action}",
    (
        string destination,
        string action,
        HttpRequest request) =>
    {
        Require.That(
            builder.Configuration["ENABLE_LAB_DEMOS"] == "1",
            "lab_disabled",
            404);

        Actor(request, "organizer");

        Require.That(
            new[] { "tournament", "scores", "results", "simulator" }
                .Contains(destination) &&
            action is "pause" or "resume",
            "invalid_lab_command",
            400);

        store.Update((state, _) =>
        {
            if (action == "pause")
                state.PausedDestinations.Add(destination);
            else
                state.PausedDestinations.Remove(destination);
        });

        return Results.Ok();
    });

app.MapPost(
    "/ui-api/lab/reject-next-forms",
    async (
        HttpRequest request,
        IHttpClientFactory clients) =>
    {
        Require.That(
            builder.Configuration["ENABLE_LAB_DEMOS"] == "1",
            "lab_disabled",
            404);

        Actor(request, "organizer");

        var peer =
            builder.Configuration["PEER_SIMULATOR"]
            ?? "http://localhost:5104";

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            peer + "/lab/reject-next-forms");

        message.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                builder.Configuration["SIMULATOR_KEY"]
                ?? "simulator-demo");

        using var response = await clients
            .CreateClient("ui")
            .SendAsync(
                message,
                request.HttpContext.RequestAborted);

        return Results.Content(
            await response.Content.ReadAsStringAsync(),
            "application/json",
            statusCode: (int)response.StatusCode);
    });

app.MapGet(
    "/openapi.json",
    () => Results.Text(
        File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "openapi.json")),
        "application/json"));

app.MapPost(
    "/internal/events",
    (Event message, HttpRequest request) =>
    {
        Require.That(
            message.Producer is
                "tournament" or
                "scores" or
                "results" or
                "team" or
                "checking" or
                "simulator",
            "invalid_producer",
            403);

        Require.That(
            Secure(
                Key(message.Producer),
                request.Headers.Authorization
                    .ToString()
                    .Replace("Bearer ", "", StringComparison.Ordinal)),
            "invalid_producer_credential",
            403);

        store.Update(
            (state, engine) => engine.Receive(state, message));

        return Results.Accepted();
    });

app.MapGet(
    "/internal/diagnostics",
    (HttpRequest request) =>
    {
        Actor(request, "organizer");

        var state = store.Read();

        return Results.Ok(new
        {
            pending = state.Pending.Keys,
            state.Quarantine,
            preparations = state.Preparations.Values,
            reservations = state.Reservations.Values,
            pausedDestinations = state.PausedDestinations,

            cache = app.Services
                .GetRequiredService<TableSnapshotCache>()
                .Statistics,

            outboxPending = store.PendingCount(),
            outboxDeadLetter = store.DeadLetterCount()
        });
    });

app.MapGet(
    "/internal/outbox/dead-letter",
    (HttpRequest request) =>
    {
        Actor(request, "organizer");

        return Results.Ok(new
        {
            items = store.DeadLetters()
        });
    });

app.MapPost(
    "/internal/outbox/dead-letter/{eventId}/{destination}/retry",
    (
        string eventId,
        string destination,
        HttpRequest request) =>
    {
        Actor(request, "organizer");

        store.RequeueDeadLetter(eventId, destination);

        return Results.Ok(new
        {
            requeued = true,
            eventId,
            destination
        });
    });

if (service == "tournament")
{
    app.MapGet(
        "/tournaments",
        (HttpRequest request) =>
        {
            string? organizer = null;

            if (!string.IsNullOrWhiteSpace(
                request.Headers.Authorization))
            {
                try
                {
                    organizer = Actor(request, "organizer");
                }
                catch (RuleException)
                {
                }
            }

            return Results.Ok(
                store.Read().Tournaments.Values
                    .Where(t =>
                        t.Status != "Draft" ||
                        t.Organizer == organizer)
                    .OrderByDescending(t => t.Starts)
                    .Select(t => new
                    {
                        t.Id,
                        t.Name,
                        t.Status,
                        t.Starts,
                        t.Ends,
                        Teams = t.Teams.Count,
                        Games = t.Games.Count
                    }));
        });

    app.MapPost(
        "/tournaments",
        (Tournament tournament, HttpRequest request) =>
        {
            var actor = Actor(request, "organizer");
            var id = CommandId(request);

            return Results.Text(
                store.Update(
                    (state, engine) =>
                        engine.Create(state, tournament, actor),
                    id,
                    actor + Json.Write(tournament)),
                "application/json");
        });

    app.MapGet(
        "/tournaments/{id}",
        (string id, HttpRequest request) =>
        {
            var state = store.Read();

            Require.That(
                state.Tournaments.TryGetValue(id, out var tournament),
                "tournament_not_found",
                404);

            var organizer =
                request.Headers.Authorization.ToString() != "" &&
                TryOrganizer(request, tournament!.Organizer);

            Require.That(
                tournament!.Status != "Draft" || organizer,
                "not_published",
                404);

            if (organizer)
                return Results.Ok(tournament);

            return Results.Ok(new
            {
                tournament.Id,
                tournament.Name,
                tournament.Status,
                tournament.Revision,
                tournament.Starts,
                tournament.Ends,
                tournament.TeamSize,
                tournament.Regulation,
                tournament.TiePolicy,

                Games = tournament.Games.Select(game => new
                {
                    game.Config.Id,
                    game.Config.Type,
                    game.Status,
                    game.SolutionsDue,
                    game.AppealsDue,
                    game.SolutionsClosed,
                    game.AppealsClosed,
                    game.PreparationAttemptId,
                    game.PreparationStatus,
                    game.PreparationFailure
                }),

                Teams = tournament.Teams.Select(team => new
                {
                    team.Id,
                    team.Name
                })
            });
        });

    bool TryOrganizer(HttpRequest request, string owner)
    {
        try
        {
            return Actor(request, "organizer") == owner;
        }
        catch (RuleException)
        {
            return false;
        }
    }

    app.MapPut(
        "/tournaments/{id}/games/{game}",
        (
            string id,
            string game,
            GameConfig config,
            HttpRequest request) =>
        {
            Require.That(
                config.Id == game,
                "game_id_mismatch",
                400);

            return Execute(
                id,
                "configure",
                request,
                config: config,
                game: game);
        });

    app.MapDelete(
        "/tournaments/{id}/games/{game}",
        (string id, string game, HttpRequest request) =>
            Execute(id, "delete", request, game: game));

    app.MapPost(
        "/tournaments/{id}/teams",
        (string id, Team team, HttpRequest request) =>
            Execute(id, "register", request, team: team));

    app.MapPost(
        "/tournaments/{id}/publish",
        (string id, HttpRequest request) =>
            Execute(id, "publish", request));

    app.MapPost(
        "/tournaments/{id}/start",
        (string id, HttpRequest request) =>
            Execute(id, "start", request));

    app.MapPost(
        "/tournaments/{id}/games/{game}/start",
        (string id, string game, HttpRequest request) =>
            Execute(id, "start-game", request, game: game));

    IResult Execute(
        string id,
        string action,
        HttpRequest request,
        GameConfig? config = null,
        Team? team = null,
        string? game = null)
    {
        var actor = Actor(
            request,
            action == "register" ? "captain" : "organizer");

        Require.That(
            long.TryParse(
                request.Headers.IfMatch.ToString().Trim('"'),
                out var revision),
            "if_match_revision_required",
            428);

        var commandId = CommandId(request);

        return Results.Text(
            store.Update(
                (state, engine) =>
                    engine.Command(
                        state,
                        id,
                        action,
                        actor,
                        revision,
                        config,
                        team,
                        game),
                commandId,
                Json.Write(new
                {
                    actor,
                    id,
                    action,
                    revision,
                    config,
                    team,
                    game
                })),
            "application/json");
    }
}

if (service is "scores" or "results")
{
    app.MapGet(
        "/tournaments/{id}/table",
        (string id, TableSnapshotCache cache) =>
            Results.Text(
                cache.Get(id),
                "application/json"));

    app.MapGet(
        "/tournaments/{id}/stream",
        async (string id, HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            string? previous = null;

            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var current = app.Services
                        .GetRequiredService<TableSnapshotCache>()
                        .Get(id);

                    if (current != previous)
                    {
                        await context.Response.WriteAsync(
                            "data: " + current + "\n\n",
                            context.RequestAborted);

                        previous = current;
                    }
                    else
                    {
                        await context.Response.WriteAsync(
                            ": heartbeat\n\n",
                            context.RequestAborted);
                    }

                    await context.Response.Body.FlushAsync(
                        context.RequestAborted);

                    await Task.Delay(
                        500,
                        context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
                when (context.RequestAborted.IsCancellationRequested)
            {
            }
        });
}

app.Run();

static string CommandId(HttpRequest request)
{
    var id = request.Headers["Idempotency-Key"].ToString();

    Require.That(
        !string.IsNullOrWhiteSpace(id),
        "idempotency_key_required",
        400);

    return id;
}

static bool Secure(string expected, string actual) =>
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
        SHA256.HashData(Encoding.UTF8.GetBytes(actual)));

record ServiceName(string Value);

sealed class DeadlineWorker(
    IStateStore store,
    ILogger<DeadlineWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(250));

        do
        {
            try
            {
                store.Update(
                    (state, engine) =>
                        engine.Tick(state, DateTimeOffset.UtcNow));
            }
            catch (Exception exception)
            {
                log.LogError(
                    exception,
                    "Deadline/state processing failed; will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

sealed class DeliveryWorker(
    IStateStore stateStore,
    IOutboxStore outboxStore,
    ServiceName service,
    IConfiguration config,
    ILogger<DeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        string Address(string destination)
        {
            var port = destination switch
            {
                "tournament" => 5101,
                "scores" => 5102,
                "results" => 5103,
                _ => 5104
            };

            return config[
                "PEER_" + destination.ToUpperInvariant()]
                ?? $"http://localhost:{port}";
        }

        var dispatcher = new DeliveryDispatcher(
            stateStore,
            outboxStore,
            http,
            Address,
            config[
                "KEY_" + service.Value.ToUpperInvariant()]
                ?? service.Value + "-integration-demo");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log.LogError(
                    exception,
                    "Delivery failed; durable outbox retained");
            }

            await Task.Delay(250, stoppingToken);
        }
    }
}