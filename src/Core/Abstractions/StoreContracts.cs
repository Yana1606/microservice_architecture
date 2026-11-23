namespace MathTournament;

public interface IStateStore
{
    State Read();

    string Update(
        Action<State, Engine> operation,
        string? commandId = null,
        string? fingerprint = null);
}

public interface IOutboxStore
{
    List<Delivery> Pending(
        DateTimeOffset? dueAt = null,
        int perDestination = 25);

    long PendingCount();

    long DeadLetterCount();

    List<OutboxDeadLetter> DeadLetters(int limit = 100);

    void MoveToDeadLetter(
        Delivery delivery,
        string reason,
        DateTimeOffset? now = null);

    void RequeueDeadLetter(
        string eventId,
        string destination);

    void Delivered(
        Delivery delivery,
        string? error = null,
        DateTimeOffset? now = null);
}

public interface IPublicSnapshotStore
{
    event Action<IReadOnlyList<string>>? ProjectionCommitted;

    long PublicVersion(string id);

    (long Revision, string Json) PublicSnapshot(string id);
}

/// <summary>
/// Defines routing rules for outgoing domain events.
/// </summary>
public interface IEventRouter
{
    IReadOnlyList<string> Resolve(Event message);
}

public sealed record OutboxDeadLetter(
    string EventId,
    string Destination,
    string Reason,
    DateTimeOffset DeadLetteredAt,
    int Attempts);