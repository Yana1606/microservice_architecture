using MathTournament;
using System.Net.Http.Headers;
using System.Text;

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

// Test fixture only: does not implement team workflows or jury assignment.
var builder = WebApplication.CreateBuilder(args);

var path = builder.Configuration["DB"] ?? "data/simulator.db";
var store = new Store(path, "simulator");

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IStateStore>(store);
builder.Services.AddSingleton<IOutboxStore>(store);

builder.Services.AddHostedService<SimulatorDelivery>();
builder.Services.AddHostedService<SimulatorRecovery>();

var app = builder.Build();

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
    catch (System.Text.Json.JsonException)
    {
        context.Response.StatusCode = 400;

        await context.Response.WriteAsJsonAsync(
            new { code = "invalid_json" });
    }
});

app.MapGet(
    "/health",
    () => new
    {
        status = "ok",
        mode = "test-simulator"
    });

app.MapPost(
    "/internal/events",
    (Event message, HttpRequest request) =>
    {
        Validation.Envelope(message);

        var expected =
            builder.Configuration[
                "KEY_" + message.Producer.ToUpperInvariant()]
            ?? message.Producer + "-integration-demo";

        if (request.Headers.Authorization != "Bearer " + expected)
            return Results.Unauthorized();

        if (message.Type is "TournamentStarted" or "GameStarted")
            Validation.Event(message);

        store.Update((state, engine) =>
        {
            if (message.Type is
                "TournamentStarted" or
                "GameStarted" or
                "ReserveForms" or
                "ReleaseForms")
            {
                engine.Receive(state, message);
                return;
            }

            var json = Json.Write(message);

            if (state.Receipts.TryGetValue(message.Id, out var old))
            {
                Require.That(
                    old == json,
                    "event_id_payload_conflict");
            }
            else
            {
                state.Receipts[message.Id] = json;
            }
        });

        return Results.Accepted();
    });

app.MapPost(
    "/lab/reject-next-forms",
    (HttpRequest request) =>
    {
        Require.That(
            builder.Configuration["ENABLE_LAB_DEMOS"] == "1",
            "lab_disabled",
            404);

        Require.That(
            request.Headers.Authorization ==
                "Bearer " +
                (builder.Configuration["SIMULATOR_KEY"]
                    ?? "simulator-demo"),
            "forbidden",
            403);

        store.Update((state, _) =>
            state.RejectNextForms = true);

        return Results.Ok(new { armed = true });
    });

app.MapGet(
    "/lab/reservations",
    (HttpRequest request) =>
    {
        Require.That(
            builder.Configuration["ENABLE_LAB_DEMOS"] == "1",
            "lab_disabled",
            404);

        Require.That(
            request.Headers.Authorization ==
                "Bearer " +
                (builder.Configuration["SIMULATOR_KEY"]
                    ?? "simulator-demo"),
            "forbidden",
            403);

        return Results.Ok(new
        {
            reservations = store.Read().Reservations.Values
        });
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

        Require.That(
            request.Headers.Authorization ==
                "Bearer " +
                (builder.Configuration["SIMULATOR_KEY"]
                    ?? "simulator-demo"),
            "forbidden",
            403);

        Require.That(
            destination == "tournament" &&
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
    "/fixtures/{tournament}/{game}",
    async (
        string tournament,
        string game,
        HttpRequest request) =>
    {
        if (request.Headers.Authorization !=
            "Bearer " +
            (builder.Configuration["SIMULATOR_KEY"]
                ?? "simulator-demo"))
        {
            return Results.Unauthorized();
        }

        var receiptKey =
            "fixture/" + tournament + "/" + game;

        // Persist the immutable fixture before sending.
        // The endpoint is safe to retry after interruption.
        store.Update((state, _) =>
        {
            if (state.Receipts.ContainsKey(receiptKey))
                return;

            var started = state.Receipts.Values
                .Where(value => value.StartsWith('{'))
                .Select(Json.Read<Event>)
                .LastOrDefault(message =>
                    message.Type == "TournamentStarted" &&
                    message.TournamentId == tournament);

            var gameStarted = state.Receipts.Values
                .Where(value => value.StartsWith('{'))
                .Select(Json.Read<Event>)
                .Any(message =>
                    message.Type == "GameStarted" &&
                    message.TournamentId == tournament &&
                    message.GameId == game);

            Require.That(
                started != null && gameStarted,
                "wait_for_game_started");

            var tournamentData = started!.Body<Tournament>();

            var gameData = tournamentData.Games
                .Single(x => x.Config.Id == game);

            var events = new List<Event>();
            var anonymous = new List<string>();
            var verdictIds = new List<string>();

            for (var i = 0; i < tournamentData.Teams.Count; i++)
            {
                for (var j = 0; j < gameData.Config.Tasks.Count; j++)
                {
                    var anonymousId = Guid.NewGuid().ToString("N");
                    anonymous.Add(anonymousId);

                    events.Add(Event.Create(
                        "SolutionBound",
                        "team",
                        tournament,
                        game,
                        new Binding(
                            anonymousId,
                            tournamentData.Teams[i].Id,
                            gameData.Config.Tasks[j].Id,
                            "normal")));

                    var initial = Event.Create(
                        "VerdictRecorded",
                        "checking",
                        tournament,
                        game,
                        new Verdict(
                            anonymousId,
                            1,
                            i == 0 && j == 0
                                ? "Incorrect"
                                : i == 0
                                    ? "Correct"
                                    : "Incorrect",
                            tournamentData.Jury[0],
                            "Jury"));

                    events.Add(initial);
                    verdictIds.Add(initial.Id);

                    if (j == 0)
                    {
                        var appeal = Event.Create(
                            "VerdictRecorded",
                            "checking",
                            tournament,
                            game,
                            new Verdict(
                                anonymousId,
                                2,
                                i == 0
                                    ? "AppealUpheld"
                                    : "AppealRejected",
                                tournamentData.Senior,
                                "Senior"));

                        events.Add(appeal);
                        verdictIds.Add(appeal.Id);
                    }
                }
            }

            events.Add(Event.Create(
                "TeamSealed",
                "team",
                tournament,
                game,
                new TeamSeal(anonymous)));

            events.Add(Event.Create(
                "ChecksSealed",
                "checking",
                tournament,
                game,
                new CheckSeal(verdictIds)));

            // Reverse delivery intentionally:
            // seals and appeals arrive before bindings and initial verdicts.
            events.Reverse();

            state.Receipts[receiptKey] = Json.Write(events);
        });

        var fixture = Json.Read<List<Event>>(
            store.Read().Receipts[receiptKey]);

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        foreach (var message in fixture)
        {
            using var outgoing = new HttpRequestMessage(
                HttpMethod.Post,
                (builder.Configuration["SCORES_URL"]
                    ?? "http://localhost:5102") +
                "/internal/events");

            outgoing.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    builder.Configuration[
                        "KEY_" + message.Producer.ToUpperInvariant()]
                    ?? message.Producer + "-integration-demo");

            outgoing.Content = new StringContent(
                Json.Write(message),
                Encoding.UTF8,
                "application/json");

            using var response = await http.SendAsync(outgoing);
            response.EnsureSuccessStatusCode();
        }

        return Results.Ok(new
        {
            sent = fixture.Count,
            replaySafe = true
        });
    });

app.Run();