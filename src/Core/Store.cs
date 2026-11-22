using Microsoft.Data.Sqlite;
namespace MathTournament;

// One database per service. BEGIN IMMEDIATE serializes competing writers, including other processes.
// State, inbox, deferred messages, command receipts and outbox commit together.
public sealed class Store
{
    private readonly string connectionString;
    private readonly string service;
    public event Action<IReadOnlyList<string>>? ProjectionCommitted;
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
        migration.CommandText=ReadMigration("003_public_snapshots.sql");migration.ExecuteNonQuery();
        // Check the exact migration marker, not MAX(version): later independent schema
        // additions must never make us skip the legacy game-key migration.
        migration.Parameters.Clear();
        migration.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version=4";
        if (Convert.ToInt32(migration.ExecuteScalar()) == 0)
        {
            migration.CommandText = "SELECT json FROM state WHERE id=1";
            var state = Json.Read<State>((string)migration.ExecuteScalar()!);
            UpgradeGameKeys(state);
            migration.CommandText = "UPDATE state SET json=$json WHERE id=1";
            migration.Parameters.AddWithValue("$json", Json.Write(state)); migration.ExecuteNonQuery();
            migration.Parameters.Clear();
            migration.CommandText = ReadMigration("004_game_keys.sql"); migration.ExecuteNonQuery();
        }

        // The outbox extension is deliberately tracked separately from the ordered
        // legacy schema_version sequence. This keeps a v3 database genuinely v3 when
        // the v4 data migration fails and rolls back (including ambiguous legacy keys).
        EnsureDeadLetterColumns(migration);
        migration.Parameters.Clear();
        migration.CommandText = ReadMigration("005_outbox_dead_letter.sql");
        migration.ExecuteNonQuery();
        tx.Commit();
    }
    private static void EnsureDeadLetterColumns(SqliteCommand command)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        command.Parameters.Clear();
        command.CommandText = "PRAGMA table_info(outbox)";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        command.Parameters.Clear();
        if (!columns.Contains("dead_lettered"))
        {
            command.CommandText = "ALTER TABLE outbox ADD COLUMN dead_lettered INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("dead_lettered_ms"))
        {
            command.CommandText = "ALTER TABLE outbox ADD COLUMN dead_lettered_ms INTEGER";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("dead_letter_reason"))
        {
            command.CommandText = "ALTER TABLE outbox ADD COLUMN dead_letter_reason TEXT";
            command.ExecuteNonQuery();
        }
    }

    private static void UpgradeGameKeys(State state)
    {
        var keys = state.Tournaments.Values.SelectMany(t => t.Games.Select(g =>
            (Old: t.Id + "/" + g.Config.Id, New: State.Key(t.Id, g.Config.Id))))
            .GroupBy(x => x.Old).ToDictionary(g => g.Key, g => g.Select(x => x.New).Distinct().ToArray());
        Dictionary<string, T> Upgrade<T>(Dictionary<string, T> source)
        {
            var result = new Dictionary<string, T>();
            foreach (var (oldKey, value) in source)
            {
                var key = oldKey;
                if (keys.TryGetValue(oldKey, out var candidates))
                {
                    // An already-colliding legacy row has no reliable owner. Roll back the upgrade.
                    Require.That(candidates.Length == 1, "ambiguous_legacy_game_key");
                    key = candidates[0];
                }
                Require.That(result.TryAdd(key, value), "ambiguous_legacy_game_key");
            }
            return result;
        }
        state.Scores = Upgrade(state.Scores);
        state.Finals = Upgrade(state.Finals);
        state.Snapshots = Upgrade(state.Snapshots);
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
        var changed=new List<string>();
        if(service is "scores" or "results")
        {
            foreach(var id in state.Tournaments.Keys)
            {
                var snapshot=Json.Write(PublicTable.Build(state,id));
                q.Parameters.Clear();q.CommandText="SELECT json FROM public_snapshots WHERE tournament_id=$id";q.Parameters.AddWithValue("$id",id);
                if((string?)q.ExecuteScalar()==snapshot)continue;
                q.CommandText="INSERT INTO public_snapshots(tournament_id,revision,json) VALUES($id,1,$json) ON CONFLICT(tournament_id) DO UPDATE SET revision=revision+1,json=excluded.json";q.Parameters.AddWithValue("$json",snapshot);q.ExecuteNonQuery();changed.Add(id);
            }
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
        if(changed.Count>0)ProjectionCommitted?.Invoke(changed);
        if (rejection != null) throw rejection;
        return response;
    }
    public long PublicVersion(string id)
    {
        using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT revision FROM public_snapshots WHERE tournament_id=$id";q.Parameters.AddWithValue("$id",id);return (long?)q.ExecuteScalar()??0;
    }
    public (long Revision,string Json) PublicSnapshot(string id)
    {
        using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT revision,json FROM public_snapshots WHERE tournament_id=$id";q.Parameters.AddWithValue("$id",id);using var r=q.ExecuteReader();
        return r.Read()?(r.GetInt64(0),r.GetString(1)):(0,Json.Write(new{status="WaitingForTournament",rows=Array.Empty<object>()}));
    }
    public static string[] Routes(Event e) => e.Type switch
    {
        "ReserveScoreboard" or "ReleaseScoreboard" => ["scores"],
        "ReserveForms" or "ReleaseForms" => ["simulator"],
        "ScoreboardReserved" or "ScoreboardPreparationRejected" or "ScoreboardReleased" or "FormsReserved" or "FormsPreparationRejected" or "FormsReleased" => ["tournament"],
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
              FROM outbox WHERE sent=0 AND dead_lettered=0 AND ($due IS NULL OR next_attempt_ms <= $due)
            ) WHERE destination_row <= $limit ORDER BY destination,position
            """;
        q.Parameters.AddWithValue("$due", (object?)dueAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        q.Parameters.AddWithValue("$limit", perDestination);
        using var r = q.ExecuteReader(); var rows = new List<Delivery>();
        while (r.Read()) rows.Add(new Delivery(Json.Read<Event>(r.GetString(0)), r.GetString(1))); return rows;
    }
    public long PendingCount()
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM outbox WHERE sent=0 AND dead_lettered=0";
        return (long)q.ExecuteScalar()!;
    }
    public long DeadLetterCount()
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM outbox WHERE dead_lettered=1";
        return (long)q.ExecuteScalar()!;
    }
    public List<OutboxDeadLetter> DeadLetters(int limit = 100)
    {
        Require.That(limit is >= 1 and <= 500, "invalid_dead_letter_limit", 400);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,destination,dead_letter_reason,dead_lettered_ms,attempts
            FROM outbox WHERE dead_lettered=1
            ORDER BY dead_lettered_ms DESC,id,destination LIMIT $limit
            """;
        q.Parameters.AddWithValue("$limit", limit);
        using var r = q.ExecuteReader(); var rows = new List<OutboxDeadLetter>();
        while (r.Read())
        {
            rows.Add(new OutboxDeadLetter(r.GetString(0), r.GetString(1), r.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), r.GetInt32(4)));
        }
        return rows;
    }
    public void MoveToDeadLetter(Delivery d, string reason, DateTimeOffset? now = null)
    {
        Require.That(!string.IsNullOrWhiteSpace(reason), "dead_letter_reason_required", 400);
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE outbox SET dead_lettered=1,dead_lettered_ms=$now,dead_letter_reason=$reason,
              attempts=attempts+1,last_error=$reason,next_attempt_ms=0
            WHERE id=$id AND destination=$dest AND sent=0 AND dead_lettered=0
            """;
        q.Parameters.AddWithValue("$now", (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$reason", reason.Length > 1000 ? reason[..1000] : reason);
        q.Parameters.AddWithValue("$id", d.Event.Id); q.Parameters.AddWithValue("$dest", d.Destination);
        q.ExecuteNonQuery();
    }
    public void RequeueDeadLetter(string eventId, string destination)
    {
        using var c = Open(); using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE outbox SET dead_lettered=0,dead_lettered_ms=NULL,dead_letter_reason=NULL,
              next_attempt_ms=0,last_error=NULL
            WHERE id=$id AND destination=$dest AND sent=0 AND dead_lettered=1
            """;
        q.Parameters.AddWithValue("$id", eventId); q.Parameters.AddWithValue("$dest", destination);
        Require.That(q.ExecuteNonQuery() == 1, "dead_letter_not_found", 404);
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

public sealed record OutboxDeadLetter(string EventId, string Destination, string Reason, DateTimeOffset DeadLetteredAt, int Attempts);

