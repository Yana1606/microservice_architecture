using Microsoft.Data.Sqlite;
namespace MathTournament;

// One database per service. BEGIN IMMEDIATE serializes competing writers, including other processes.
// State, inbox, deferred messages, command receipts and outbox commit together.
public sealed class Store
{
    private readonly string connectionString;
    private readonly string service;
    public Store(string path, string service)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        this.service = service;
        using var c = Open();
        using var journal = c.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=WAL"; journal.ExecuteNonQuery();
        using var tx = c.BeginTransaction(deferred: false);
        using var migration = c.CreateCommand(); migration.Transaction = tx;
        migration.CommandText = Migration.Replace("PRAGMA journal_mode=WAL;", "", StringComparison.Ordinal);
        migration.ExecuteNonQuery();
        migration.CommandText = "SELECT MAX(version) FROM schema_version";
        if (Convert.ToInt32(migration.ExecuteScalar()) < 2)
        {
            migration.CommandText = ReadMigration("002_delivery_retry.sql"); migration.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public static string Migration
    {
        get
        {
            return ReadMigration("001_initial.sql");
        }
    }
    private static string ReadMigration(string name)
    {
        var assembly = typeof(Store).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(x => x.EndsWith(name, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public State Read()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT json FROM state WHERE id=1";
        return Json.Read<State>((string)q.ExecuteScalar()!);
    }
    public string Update(Action<State, Engine> operation, string? commandId = null, string? fingerprint = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        using var q = c.CreateCommand(); q.Transaction = tx;
        if (commandId != null)
        {
            q.CommandText = "SELECT fingerprint,response FROM commands WHERE id=$id"; q.Parameters.AddWithValue("$id", commandId);
            using var reader = q.ExecuteReader();
            if (reader.Read())
            {
                Require.That(reader.GetString(0) == fingerprint, "idempotency_key_reused");
                var saved = reader.GetString(1);
                using var result = System.Text.Json.JsonDocument.Parse(saved);
                if (result.RootElement.TryGetProperty("code", out var code))
                    throw new RuleException(code.GetString()!, result.RootElement.GetProperty("status").GetInt32());
                return saved;
            }
        }
        q.Parameters.Clear(); q.CommandText = "SELECT json FROM state WHERE id=1";
        var state = Json.Read<State>((string)q.ExecuteScalar()!); var engine = new Engine(service);
        RuleException? rejection = null;
        try { operation(state, engine); }
        catch (RuleException ex) when (engine.Events.Any(e => e.Type == "RegistrationRejected")) { rejection = ex; }
        q.CommandText = "UPDATE state SET json=$json WHERE id=1"; q.Parameters.AddWithValue("$json", Json.Write(state)); q.ExecuteNonQuery();
        foreach (var e in engine.Events)
        foreach (var destination in Routes(e))
        {
            q.Parameters.Clear(); q.CommandText = "INSERT INTO outbox(id,destination,json) VALUES($id,$dest,$json)";
            q.Parameters.AddWithValue("$id", e.Id); q.Parameters.AddWithValue("$dest", destination); q.Parameters.AddWithValue("$json", Json.Write(e)); q.ExecuteNonQuery();
        }
        var response = rejection == null
            ? Json.Write(new { accepted = true, tournaments = state.Tournaments.Values.Select(t => new { t.Id, t.Revision, t.Status }) })
            : Json.Write(new { accepted = false, code = rejection.Message, status = rejection.Status });
        if (commandId != null)
        {
            q.Parameters.Clear(); q.CommandText = "INSERT INTO commands VALUES($id,$fp,$response)";
            q.Parameters.AddWithValue("$id", commandId); q.Parameters.AddWithValue("$fp", fingerprint); q.Parameters.AddWithValue("$response", response); q.ExecuteNonQuery();
        }
        tx.Commit();
        if (rejection != null) throw rejection;
        return response;
    }
    public static string[] Routes(Event e) => e.Type switch
    {
        "TournamentStarted" or "GameStarted" or "SolutionsClosed" or "AppealsClosed" => ["scores", "results", "simulator"],
        "TournamentCreated" or "GamesConfigured" or "TournamentPublished" or "TeamRegistered" or "RegistrationRejected" or "TournamentCompleted" => ["simulator"],
        "ScoresReady" => ["results"],
        "GameFinalized" or "WinnerDetermined" => ["tournament", "scores", "simulator"],
        "ScoreChanged" or "AppealScoreChanged" or "InitialResultsCreated" or "TournamentPointsUpdated" => ["simulator"],
        _ => []
    };
    public List<Delivery> Pending(DateTimeOffset? dueAt = null, int perDestination = 25)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            SELECT json,destination FROM (
              SELECT json,destination,rowid AS position,
                ROW_NUMBER() OVER(PARTITION BY destination ORDER BY rowid) AS destination_row
              FROM outbox WHERE sent=0 AND ($due IS NULL OR next_attempt_ms <= $due)
            ) WHERE destination_row <= $limit ORDER BY destination,position
            """;
        q.Parameters.AddWithValue("$due", (object?)dueAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        q.Parameters.AddWithValue("$limit", perDestination);
        using var r = q.ExecuteReader(); var rows = new List<Delivery>();
        while (r.Read()) rows.Add(new Delivery(Json.Read<Event>(r.GetString(0)), r.GetString(1))); return rows;
    }
    public long PendingCount()
    {
        using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent=0";
        return (long)q.ExecuteScalar()!;
    }
    public void Delivered(Delivery d, string? error = null, DateTimeOffset? now = null)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE outbox SET sent=$sent,attempts=attempts+1,last_error=$error,
              next_attempt_ms=CASE WHEN $sent=1 THEN 0 ELSE $now + MIN(60000,1000 * (1 << MIN(attempts,6))) END
            WHERE id=$id AND destination=$dest
            """;
        q.Parameters.AddWithValue("$now", (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$sent", error == null ? 1 : 0); q.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        q.Parameters.AddWithValue("$id", d.Event.Id); q.Parameters.AddWithValue("$dest", d.Destination); q.ExecuteNonQuery();
    }
}
