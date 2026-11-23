using Microsoft.Extensions.Caching.Memory;

namespace MathTournament;

/// <summary>
/// Caches immutable public-table JSON while validating
/// the durable projection revision.
/// </summary>
public sealed class TableSnapshotCache : IDisposable
{
    private const string KeyPrefix = "public-table:";

    private readonly IPublicSnapshotStore snapshots;
    private readonly IMemoryCache cache;
    private readonly TimeSpan ttl;

    private long hits;
    private long misses;

    private sealed record Entry(long Revision, string Json);

    public TableSnapshotCache(
        IPublicSnapshotStore snapshots,
        IMemoryCache cache)
        : this(snapshots, cache, TimeSpan.FromSeconds(10))
    {
    }

    public TableSnapshotCache(
        IPublicSnapshotStore snapshots,
        IMemoryCache cache,
        TimeSpan ttl)
    {
        this.snapshots = snapshots;
        this.cache = cache;

        this.ttl = ttl > TimeSpan.Zero
            ? ttl
            : throw new ArgumentOutOfRangeException(nameof(ttl));

        snapshots.ProjectionCommitted += Invalidate;
    }

    private void Invalidate(
        IReadOnlyList<string> tournamentIds)
    {
        foreach (var id in tournamentIds)
            cache.Remove(KeyPrefix + id);
    }

    public string Get(string tournamentId)
    {
        var key = KeyPrefix + tournamentId;

        // Durable revision check also detects writes from another process.
        var currentRevision = snapshots.PublicVersion(tournamentId);

        if (cache.TryGetValue<Entry>(key, out var entry) &&
            entry is not null &&
            entry.Revision == currentRevision)
        {
            Interlocked.Increment(ref hits);
            return entry.Json;
        }

        Interlocked.Increment(ref misses);

        var snapshot = snapshots.PublicSnapshot(tournamentId);

        cache.Set(
            key,
            new Entry(snapshot.Revision, snapshot.Json),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
                Size = 1
            });

        return snapshot.Json;
    }

    public object Statistics => new
    {
        hits = Interlocked.Read(ref hits),
        misses = Interlocked.Read(ref misses),
        ttlSeconds = ttl.TotalSeconds
    };

    public void Dispose() =>
        snapshots.ProjectionCommitted -= Invalidate;
}