using MathTournament;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Tests;

public class CacheTests
{
    [Theory]
    [InlineData("Correct", 10)]
    [InlineData("Incorrect", 0)]
    public void RealVerdictEventInvalidatesEvenWhenOnlyStatusChanges(
        string outcome,
        int points)
    {
        var flow = new SagaTests.Flow();

        var request = flow.ReserveFirst();

        flow.Forms.Receive(flow.F, request);
        flow.Coordinator.Receive(
            flow.T,
            flow.Forms.Events.Last());

        flow.Scores.Receive(
            flow.S,
            flow.Coordinator.Events.Last());

        var store = new Store(
            Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid() + ".db"),
            "scores");

        store.Update((state, _) =>
        {
            state.Tournaments = flow.S.Tournaments;
            state.Scores = flow.S.Scores;
            state.Reservations = flow.S.Reservations;
        });

        using var memory =
            new MemoryCache(new MemoryCacheOptions());

        using var cache =
            new TableSnapshotCache(store, memory);

        var before = cache.Get("t");

        store.Update((state, engine) =>
            engine.Receive(
                state,
                Event.Create(
                    "SolutionBound",
                    "team",
                    "t",
                    "g",
                    new Binding(
                        "anon",
                        "a",
                        "q",
                        "normal"))));

        store.Update((state, engine) =>
            engine.Receive(
                state,
                Event.Create(
                    "VerdictRecorded",
                    "checking",
                    "t",
                    "g",
                    new Verdict(
                        "anon",
                        1,
                        outcome,
                        "j",
                        "Jury"))));

        var after = cache.Get("t");

        Assert.NotEqual(before, after);

        Assert.Contains(
            "\"status\":\"" + outcome + "\"",
            after);

        Assert.Contains(
            "\"points\":" + points,
            after);
    }

    [Fact]
    public void CacheHitsInvalidatesAfterCommitAndSeparatesTournaments()
    {
        var store = new Store(
            Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid() + ".db"),
            "scores");

        using var memory = new MemoryCache(
            new MemoryCacheOptions
            {
                SizeLimit = 20
            });

        using var cache =
            new TableSnapshotCache(store, memory);

        store.Update((state, _) => Seed(state, "a"));

        var first = cache.Get("a");

        Assert.Equal(first, cache.Get("a"));

        var stats = Json.Write(cache.Statistics);

        Assert.Contains("\"hits\":1", stats);
        Assert.Contains("\"misses\":1", stats);

        store.Update((state, _) =>
            state.Scores["a/g"]
                .Cells["team"]["task"].Points = 10);

        var updated = cache.Get("a");

        Assert.NotEqual(first, updated);
        Assert.Contains("\"points\":10", updated);

        store.Update((state, _) => Seed(state, "b"));

        Assert.NotEqual(updated, cache.Get("b"));
        Assert.Equal(updated, cache.Get("a"));

        Assert.Throws<Exception>(() =>
            store.Update((state, _) =>
            {
                state.Scores["a/g"]
                    .Cells["team"]["task"].Points = 99;

                throw new Exception("rollback");
            }));

        Assert.Equal(updated, cache.Get("a"));

        // A second Store instance sees the revision committed by the first one.
        store.Update((state, _) =>
            state.Finals["a/g"] =
                [new("team", 10, 1, 100)]);

        Assert.Contains(
            "\"total\":100",
            cache.Get("a"));
    }

    [Fact]
    public async Task ConcurrentReadsNeverKeepStaleRefillAfterCommittedWrite()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid() + ".db");

        var store = new Store(path, "scores");

        store.Update((state, _) => Seed(state, "a"));

        using var memory =
            new MemoryCache(new MemoryCacheOptions());

        using var cache = new TableSnapshotCache(
            store,
            memory,
            TimeSpan.FromMilliseconds(40));

        var readers = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 20; i++)
                    cache.Get("a");
            }))
            .ToArray();

        new Store(path, "scores").Update((state, _) =>
            state.Scores["a/g"]
                .Cells["team"]["task"].Points = 17);

        await Task.WhenAll(readers);

        Assert.Contains(
            "\"points\":17",
            cache.Get("a"));

        var before = Json.Write(cache.Statistics);

        await Task.Delay(80);

        cache.Get("a");

        Assert.NotEqual(
            before,
            Json.Write(cache.Statistics));
    }

    [Fact]
    public void NoOpAndPrivateStateUpdatesDoNotRebuildPublicSnapshot()
    {
        var store = new Store(
            Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid() + ".db"),
            "scores");

        store.Update((state, _) => Seed(state, "a"));

        var initialRevision = store.PublicVersion("a");
        var projectionEvents = 0;

        store.ProjectionCommitted +=
            _ => projectionEvents++;

        // A true no-op must not produce a new snapshot revision.
        store.Update((_, _) => { });

        // This metadata is not displayed in the public table.
        store.Update((state, _) =>
            state.PausedDestinations.Add(
                "temporary-destination"));

        Assert.Equal(
            initialRevision,
            store.PublicVersion("a"));

        Assert.Equal(0, projectionEvents);
    }

    private static void Seed(State state, string id)
    {
        state.Tournaments[id] = new()
        {
            Id = id,
            Name = id,

            Teams =
            [
                new(
                    "team",
                    "Team",
                    "captain",
                    ["captain", "1", "2", "3"])
            ],

            Games =
            [
                new()
                {
                    Config = new(
                        "g",
                        "math",
                        [
                            new(
                                "task",
                                "s",
                                "r",
                                10,
                                0)
                        ],
                        new()
                        {
                            ["normal"] = 1
                        },
                        1,
                        2)
                }
            ]
        };

        state.Scores[id + "/g"] = new()
        {
            Cells = new()
            {
                ["team"] = new()
                {
                    ["task"] = new()
                }
            }
        };
    }
}