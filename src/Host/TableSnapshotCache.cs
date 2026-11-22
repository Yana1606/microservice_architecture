using Microsoft.Extensions.Caching.Memory;
namespace MathTournament;
// Both GET and SSE use immutable JSON. Checking the durable version prevents stale refill races.
public sealed class TableSnapshotCache : IDisposable
{
    private readonly Store store;
    private readonly IMemoryCache cache;
    private long hits,misses;
    private readonly TimeSpan ttl;
    private sealed record Entry(long Revision,string Json);
    public TableSnapshotCache(Store store,IMemoryCache cache):this(store,cache,TimeSpan.FromSeconds(10)){}
    public TableSnapshotCache(Store store,IMemoryCache cache,TimeSpan ttl)
    {this.store=store;this.cache=cache;this.ttl=ttl;store.ProjectionCommitted+=Invalidate;}
    private void Invalidate(IReadOnlyList<string> ids){foreach(var id in ids)cache.Remove("public-table:"+id);}
    public string Get(string id)
    {
        var key="public-table:"+id;var revision=store.PublicVersion(id);
        if(cache.TryGetValue<Entry>(key,out var entry)&&entry!.Revision==revision){Interlocked.Increment(ref hits);return entry.Json;}
        Interlocked.Increment(ref misses);var snapshot=store.PublicSnapshot(id);
        cache.Set(key,new Entry(snapshot.Revision,snapshot.Json),new MemoryCacheEntryOptions{AbsoluteExpirationRelativeToNow=ttl,Size=1});return snapshot.Json;
    }
    public object Statistics=>new{hits=Interlocked.Read(ref hits),misses=Interlocked.Read(ref misses),ttlSeconds=ttl.TotalSeconds};
    public void Dispose()=>store.ProjectionCommitted-=Invalidate;
}
