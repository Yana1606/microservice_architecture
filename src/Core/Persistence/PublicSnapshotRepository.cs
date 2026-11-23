using Microsoft.Data.Sqlite;

namespace MathTournament.Persistence;

/// <summary>
/// Persists public projections and their monotonically increasing revisions.
/// </summary>
internal sealed class PublicSnapshotRepository(SqliteDatabase database)
{
    public static Dictionary<string, string> CaptureFingerprints(State state)
    {
        return state.Tournaments.Keys.ToDictionary(
            id => id,
            id => Fingerprint(state, id),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<string> SaveChanged(
        SqliteConnection connection,
        SqliteTransaction transaction,
        State state,
        IReadOnlyDictionary<string, string> previousFingerprints)
    {
        var existingIds = new HashSet<string>(StringComparer.Ordinal);

        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT tournament_id FROM public_snapshots";

            using var reader = query.ExecuteReader();

            while (reader.Read())
                existingIds.Add(reader.GetString(0));
        }

        var currentIds = state.Tournaments.Keys
            .ToHashSet(StringComparer.Ordinal);

        var candidateIds = previousFingerprints.Keys
            .Concat(currentIds)
            .Concat(existingIds)
            .Distinct(StringComparer.Ordinal);

        var changed = new List<string>();

        foreach (var id in candidateIds)
        {
            var existedBefore =
                previousFingerprints.TryGetValue(id, out var previous);

            var existsNow = currentIds.Contains(id);

            var fingerprintChanged = !existedBefore ||
                !StringComparer.Ordinal.Equals(
                    previous,
                    existsNow ? Fingerprint(state, id) : "missing");

            // If the public inputs have not changed, do not even rebuild the table.
            if (existingIds.Contains(id) && !fingerprintChanged)
                continue;

            var json = Json.Write(PublicTable.Build(state, id));

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText =
                "SELECT json FROM public_snapshots WHERE tournament_id=$id";
            read.Parameters.AddWithValue("$id", id);

            var oldJson = (string?)read.ExecuteScalar();

            if (StringComparer.Ordinal.Equals(oldJson, json))
                continue;

            using var write = connection.CreateCommand();
            write.Transaction = transaction;

            write.CommandText = """
                INSERT INTO public_snapshots(tournament_id,revision,json)
                VALUES($id,1,$json)
                ON CONFLICT(tournament_id) DO UPDATE
                SET revision=revision+1,json=excluded.json
                """;

            write.Parameters.AddWithValue("$id", id);
            write.Parameters.AddWithValue("$json", json);

            write.ExecuteNonQuery();
            changed.Add(id);
        }

        return changed;
    }

    public long Version(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT revision FROM public_snapshots WHERE tournament_id=$id";

        command.Parameters.AddWithValue("$id", id);

        return (long?)command.ExecuteScalar() ?? 0;
    }

    public (long Revision, string Json) Read(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT revision,json FROM public_snapshots WHERE tournament_id=$id";

        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetInt64(0), reader.GetString(1))
            : (0, Json.Write(new
            {
                status = "WaitingForTournament",
                rows = Array.Empty<object>()
            }));
    }

    // These are precisely the inputs used by PublicTable.Build.
    // Private workflow metadata is intentionally excluded.
    private static string Fingerprint(State state, string tournamentId)
    {
        if (!state.Tournaments.TryGetValue(
            tournamentId,
            out var tournament))
        {
            return "missing";
        }

        var games = tournament.Games.Select(game =>
        {
            var key = State.Key(tournamentId, game.Config.Id);
            var score = state.Scores.GetValueOrDefault(key);

            var cells = score?.Cells.Select(team => new
            {
                TeamId = team.Key,
                Tasks = team.Value.Select(task => new
                {
                    TaskId = task.Key,
                    task.Value.Status,
                    task.Value.Points
                }).ToArray()
            }).ToArray();

            var finals = state.Finals.GetValueOrDefault(key)?
                .Select(row => new
                {
                    row.TeamId,
                    row.Score,
                    row.Place,
                    row.Points
                })
                .ToArray();

            return new
            {
                game.Config.Id,
                Tasks = game.Config.Tasks
                    .Select(task => task.Id)
                    .ToArray(),
                Cells = cells,
                Finals = finals
            };
        }).ToArray();

        return Json.Write(new
        {
            tournament.Id,
            Teams = tournament.Teams
                .Select(team => new { team.Id, team.Name })
                .ToArray(),
            Games = games,
            Complete = state.Winners.Contains(tournamentId)
        });
    }
}