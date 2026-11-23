using Microsoft.Data.Sqlite;

namespace MathTournament.Persistence;

/// <summary>
/// Applies database schema and data migrations atomically.
/// </summary>
internal sealed class DatabaseMigrator(SqliteDatabase database)
{
    public void Apply()
    {
        using var connection = database.Open();

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            journal.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = ReadMigration("001_initial.sql")
            .Replace(
                "PRAGMA journal_mode=WAL;",
                "",
                StringComparison.Ordinal);

        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "SELECT MAX(version) FROM schema_version";

        if (Convert.ToInt32(command.ExecuteScalar()) < 2)
        {
            command.Parameters.Clear();
            command.CommandText = ReadMigration("002_delivery_retry.sql");
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = ReadMigration("003_public_snapshots.sql");
        command.ExecuteNonQuery();

        // Check the exact marker. Do not use MAX(version):
        // migration 004 is a data migration and must run unless
        // its own marker exists.
        command.Parameters.Clear();
        command.CommandText =
            "SELECT COUNT(*) FROM schema_version WHERE version=4";

        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT json FROM state WHERE id=1";

            var state = Json.Read<State>(
                (string)command.ExecuteScalar()!);

            UpgradeGameKeys(state);

            command.Parameters.Clear();
            command.CommandText =
                "UPDATE state SET json=$json WHERE id=1";

            command.Parameters.AddWithValue(
                "$json",
                Json.Write(state));

            command.ExecuteNonQuery();

            command.Parameters.Clear();
            command.CommandText = ReadMigration("004_game_keys.sql");
            command.ExecuteNonQuery();
        }

        EnsureDeadLetterColumns(command);

        command.Parameters.Clear();
        command.CommandText = ReadMigration("005_outbox_dead_letter.sql");
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    private static void EnsureDeadLetterColumns(SqliteCommand command)
    {
        var columns = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        command.Parameters.Clear();
        command.CommandText = "PRAGMA table_info(outbox)";

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        command.Parameters.Clear();

        if (!columns.Contains("dead_lettered"))
        {
            command.CommandText =
                "ALTER TABLE outbox ADD COLUMN " +
                "dead_lettered INTEGER NOT NULL DEFAULT 0";

            command.ExecuteNonQuery();
        }

        if (!columns.Contains("dead_lettered_ms"))
        {
            command.CommandText =
                "ALTER TABLE outbox ADD COLUMN dead_lettered_ms INTEGER";

            command.ExecuteNonQuery();
        }

        if (!columns.Contains("dead_letter_reason"))
        {
            command.CommandText =
                "ALTER TABLE outbox ADD COLUMN dead_letter_reason TEXT";

            command.ExecuteNonQuery();
        }
    }

    private static void UpgradeGameKeys(State state)
    {
        var keys = state.Tournaments.Values
            .SelectMany(t => t.Games.Select(g =>
                (
                    Old: t.Id + "/" + g.Config.Id,
                    New: State.Key(t.Id, g.Config.Id)
                )))
            .GroupBy(x => x.Old)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(x => x.New)
                    .Distinct()
                    .ToArray());

        Dictionary<string, T> Upgrade<T>(
            Dictionary<string, T> source)
        {
            var result = new Dictionary<string, T>();

            foreach (var (oldKey, value) in source)
            {
                var key = oldKey;

                if (keys.TryGetValue(oldKey, out var candidates))
                {
                    // An ambiguous legacy key has no reliable owner.
                    // Roll back instead of guessing.
                    Require.That(
                        candidates.Length == 1,
                        "ambiguous_legacy_game_key");

                    key = candidates[0];
                }

                Require.That(
                    result.TryAdd(key, value),
                    "ambiguous_legacy_game_key");
            }

            return result;
        }

        state.Scores = Upgrade(state.Scores);
        state.Finals = Upgrade(state.Finals);
        state.Snapshots = Upgrade(state.Snapshots);
    }

    public static string ReadMigration(string name)
    {
        var assembly = typeof(Store).Assembly;

        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(x => x.EndsWith(
                name,
                StringComparison.Ordinal));

        using var stream =
            assembly.GetManifestResourceStream(resourceName)!;

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}