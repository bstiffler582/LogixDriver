using System.Collections.Concurrent;

namespace Logix.Tags
{
    /// <summary>
    /// Get-or-add for values produced asynchronously (e.g. read from the controller). Concurrent
    /// callers for the same key share one fetch. A failed or cancelled fetch isn't cached, so the
    /// next caller retries.
    /// </summary>
    internal sealed class AsyncCache<TKey, TValue> where TKey : notnull
    {
        private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> entries;

        public AsyncCache(IEqualityComparer<TKey>? comparer = null) =>
            entries = new ConcurrentDictionary<TKey, Lazy<Task<TValue>>>(comparer);

        public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> fetch)
        {
            var entry = entries.GetOrAdd(key, k => new Lazy<Task<TValue>>(() => fetch(k)));
            try
            {
                return await entry.Value.ConfigureAwait(false);
            }
            catch
            {
                // only evict the entry that failed, not a newer one another caller has since added
                entries.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, entry));
                throw;
            }
        }

        /// <summary>The value, if it has been fetched successfully.</summary>
        public bool TryGet(TKey key, out TValue value)
        {
            if (entries.TryGetValue(key, out var entry) && entry.IsValueCreated && entry.Value.IsCompletedSuccessfully)
            {
                value = entry.Value.Result;
                return true;
            }
            value = default!;
            return false;
        }

        public void Clear() => entries.Clear();
    }
}
