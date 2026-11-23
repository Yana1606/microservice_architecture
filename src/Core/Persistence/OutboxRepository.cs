using Microsoft.Data.Sqlite;

namespace MathTournament.Persistence;

/// <summary>
/// Owns durable delivery, retry and dead-letter persistence.
/// </summary>
internal sealed class OutboxRepository(SqliteDatabase database)
{
    public void Enqueue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Event message,
        IEnumerable<string> destinations)
    {
        foreach (var destination in destinations)
        {
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO outbox(id,destination,json) " +
                "VALUES($id,$dest,$json)";

            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$dest", destination);
            command.Parameters.AddWithValue(
                "$json",
                Json.Write(message));

            command.ExecuteNonQuery();
        }
    }

    public List<Delivery> Pending(
        DateTimeOffset? dueAt = null,
        int perDestination = 25)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT json,destination FROM (
              SELECT json,destination,rowid AS position,
                ROW_NUMBER() OVER(
                    PARTITION BY destination ORDER BY rowid
                ) AS destination_row
              FROM outbox
              WHERE sent=0
                AND dead_lettered=0
                AND ($due IS NULL OR next_attempt_ms <= $due)
            )
            WHERE destination_row <= $limit
            ORDER BY destination,position
            """;

        command.Parameters.AddWithValue(
            "$due",
            (object?)dueAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);

        command.Parameters.AddWithValue("$limit", perDestination);

        using var reader = command.ExecuteReader();

        var rows = new List<Delivery>();

        while (reader.Read())
        {
            rows.Add(new Delivery(
                Json.Read<Event>(reader.GetString(0)),
                reader.GetString(1)));
        }

        return rows;
    }

    public long PendingCount() =>
        Count(
            "SELECT COUNT(*) FROM outbox " +
            "WHERE sent=0 AND dead_lettered=0");

    public long DeadLetterCount() =>
        Count(
            "SELECT COUNT(*) FROM outbox " +
            "WHERE dead_lettered=1");

    private long Count(string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)command.ExecuteScalar()!;
    }

    public List<OutboxDeadLetter> DeadLetters(int limit)
    {
        Require.That(
            limit is >= 1 and <= 500,
            "invalid_dead_letter_limit",
            400);

        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id,destination,dead_letter_reason,
                   dead_lettered_ms,attempts
            FROM outbox
            WHERE dead_lettered=1
            ORDER BY dead_lettered_ms DESC,id,destination
            LIMIT $limit
            """;

        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();

        var rows = new List<OutboxDeadLetter>();

        while (reader.Read())
        {
            rows.Add(new OutboxDeadLetter(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(
                    reader.GetInt64(3)),
                reader.GetInt32(4)));
        }

        return rows;
    }

    public void MoveToDeadLetter(
        Delivery delivery,
        string reason,
        DateTimeOffset? now)
    {
        Require.That(
            !string.IsNullOrWhiteSpace(reason),
            "dead_letter_reason_required",
            400);

        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE outbox SET
              dead_lettered=1,
              dead_lettered_ms=$now,
              dead_letter_reason=$reason,
              attempts=attempts+1,
              last_error=$reason,
              next_attempt_ms=0
            WHERE id=$id
              AND destination=$dest
              AND sent=0
              AND dead_lettered=0
            """;

        command.Parameters.AddWithValue(
            "$now",
            (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());

        command.Parameters.AddWithValue(
            "$reason",
            reason.Length > 1000 ? reason[..1000] : reason);

        command.Parameters.AddWithValue("$id", delivery.Event.Id);
        command.Parameters.AddWithValue(
            "$dest",
            delivery.Destination);

        command.ExecuteNonQuery();
    }

    public void RequeueDeadLetter(
        string eventId,
        string destination)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE outbox SET
              dead_lettered=0,
              dead_lettered_ms=NULL,
              dead_letter_reason=NULL,
              next_attempt_ms=0,
              last_error=NULL
            WHERE id=$id
              AND destination=$dest
              AND sent=0
              AND dead_lettered=1
            """;

        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$dest", destination);

        Require.That(
            command.ExecuteNonQuery() == 1,
            "dead_letter_not_found",
            404);
    }

    public void Delivered(
        Delivery delivery,
        string? error,
        DateTimeOffset? now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE outbox SET
              sent=$sent,
              attempts=attempts+1,
              last_error=$error,
              next_attempt_ms=CASE
                WHEN $sent=1 THEN 0
                ELSE $now + MIN(60000,1000 * (1 << MIN(attempts,6)))
              END
            WHERE id=$id AND destination=$dest
            """;

        command.Parameters.AddWithValue(
            "$now",
            (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());

        command.Parameters.AddWithValue(
            "$sent",
            error == null ? 1 : 0);

        command.Parameters.AddWithValue(
            "$error",
            (object?)error ?? DBNull.Value);

        command.Parameters.AddWithValue("$id", delivery.Event.Id);
        command.Parameters.AddWithValue(
            "$dest",
            delivery.Destination);

        command.ExecuteNonQuery();
    }
}