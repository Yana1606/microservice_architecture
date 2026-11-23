using MathTournament.Persistence;

namespace MathTournament;

/// <summary>
/// Coordinates atomic state updates.
/// Migration, outbox and public-projection persistence are delegated
/// to dedicated components.
/// </summary>
public sealed class Store :
    IStateStore,
    IOutboxStore,
    IPublicSnapshotStore
{
    private readonly SqliteDatabase database;
    private readonly string service;

    private readonly OutboxRepository outbox;
    private readonly PublicSnapshotRepository publicSnapshots;
    private readonly IEventRouter eventRouter;

    public event Action<IReadOnlyList<string>>? ProjectionCommitted;

    public Store(string path, string service)
        : this(path, service, new EventRouter())
    {
    }

    public Store(
        string path,
        string service,
        IEventRouter eventRouter)
    {
        this.eventRouter = eventRouter
            ?? throw new ArgumentNullException(nameof(eventRouter));

        database = new SqliteDatabase(path);
        this.service = service;

        outbox = new OutboxRepository(database);
        publicSnapshots = new PublicSnapshotRepository(database);

        new DatabaseMigrator(database).Apply();
    }

    // Compatibility with existing migration tests and operational tooling.
    public static string Migration =>
        DatabaseMigrator.ReadMigration("001_initial.sql");

    public State Read()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT json FROM state WHERE id=1";

        return Json.Read<State>(
            (string)command.ExecuteScalar()!);
    }

    public string Update(
        Action<State, Engine> operation,
        string? commandId = null,
        string? fingerprint = null)
    {
        using var connection = database.Open();
        using var transaction =
            connection.BeginTransaction(deferred: false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Idempotency check.
        if (commandId != null)
        {
            command.CommandText =
                "SELECT fingerprint,response FROM commands WHERE id=$id";

            command.Parameters.AddWithValue("$id", commandId);

            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                Require.That(
                    reader.GetString(0) == fingerprint,
                    "idempotency_key_reused");

                var saved = reader.GetString(1);

                using var result =
                    System.Text.Json.JsonDocument.Parse(saved);

                if (result.RootElement.TryGetProperty(
                    "code",
                    out var code))
                {
                    throw new RuleException(
                        code.GetString()!,
                        result.RootElement
                            .GetProperty("status")
                            .GetInt32());
                }

                return saved;
            }
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT json FROM state WHERE id=1";

        var state = Json.Read<State>(
            (string)command.ExecuteScalar()!);

        // Capture only public-facing inputs before the operation.
        // Internal changes will not trigger a public-table rebuild.
        var previousFingerprints =
            service is "scores" or "results"
                ? PublicSnapshotRepository.CaptureFingerprints(state)
                : null;

        var engine = new Engine(service);
        RuleException? rejection = null;

        try
        {
            operation(state, engine);
        }
        catch (RuleException exception) when (
            engine.Events.Any(
                message => message.Type == "RegistrationRejected"))
        {
            rejection = exception;
        }

        // Save state inside the same transaction as outbox and snapshots.
        command.Parameters.Clear();
        command.CommandText =
            "UPDATE state SET json=$json WHERE id=1";

        command.Parameters.AddWithValue(
            "$json",
            Json.Write(state));

        command.ExecuteNonQuery();

        // Persist outgoing events atomically with the state update.
        foreach (var message in engine.Events)
        {
            outbox.Enqueue(
                connection,
                transaction,
                message,
                eventRouter.Resolve(message));
        }

        IReadOnlyList<string> changedSnapshots =
            Array.Empty<string>();

        if (previousFingerprints != null)
        {
            changedSnapshots = publicSnapshots.SaveChanged(
                connection,
                transaction,
                state,
                previousFingerprints);
        }

        var response = rejection == null
            ? Json.Write(new
            {
                accepted = true,
                tournaments = state.Tournaments.Values.Select(
                    tournament => new
                    {
                        tournament.Id,
                        tournament.Revision,
                        tournament.Status
                    })
            })
            : Json.Write(new
            {
                accepted = false,
                code = rejection.Message,
                status = rejection.Status
            });

        if (commandId != null)
        {
            command.Parameters.Clear();
            command.CommandText =
                "INSERT INTO commands VALUES($id,$fp,$response)";

            command.Parameters.AddWithValue("$id", commandId);
            command.Parameters.AddWithValue("$fp", fingerprint);
            command.Parameters.AddWithValue("$response", response);

            command.ExecuteNonQuery();
        }

        transaction.Commit();

        // Cache invalidation happens only after successful COMMIT.
        if (changedSnapshots.Count > 0)
            ProjectionCommitted?.Invoke(changedSnapshots);

        if (rejection != null)
            throw rejection;

        return response;
    }

    // Public snapshot operations.
    public long PublicVersion(string id) =>
        publicSnapshots.Version(id);

    public (long Revision, string Json) PublicSnapshot(string id) =>
        publicSnapshots.Read(id);

    // Outbox operations.
    public List<Delivery> Pending(
        DateTimeOffset? dueAt = null,
        int perDestination = 25) =>
        outbox.Pending(dueAt, perDestination);

    public long PendingCount() =>
        outbox.PendingCount();

    public long DeadLetterCount() =>
        outbox.DeadLetterCount();

    public List<OutboxDeadLetter> DeadLetters(int limit = 100) =>
        outbox.DeadLetters(limit);

    public void MoveToDeadLetter(
        Delivery delivery,
        string reason,
        DateTimeOffset? now = null) =>
        outbox.MoveToDeadLetter(delivery, reason, now);

    public void RequeueDeadLetter(
        string eventId,
        string destination) =>
        outbox.RequeueDeadLetter(eventId, destination);

    public void Delivered(
        Delivery delivery,
        string? error = null,
        DateTimeOffset? now = null) =>
        outbox.Delivered(delivery, error, now);

    // Compatibility method for code that previously used Store.Routes.
    public static string[] Routes(Event message) =>
        new EventRouter().Resolve(message).ToArray();
}