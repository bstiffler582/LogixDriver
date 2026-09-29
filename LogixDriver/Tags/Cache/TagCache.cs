using libplctag;
using System.Collections.Concurrent;

namespace Logix.Tags
{
    public interface ITagCache
    {
        public INativeTag GetOrAdd(string tagPath, Func<INativeTag> factory);
        public void Flush();
    }

    internal class TagCache : ITagCache
    {
        // Lazy ensures the factory runs once per path even when concurrent callers race on a miss,
        // so no orphaned native tag handles are created.
        private readonly ConcurrentDictionary<string, Lazy<INativeTag>> tagCache = new();

        public INativeTag GetOrAdd(string tagPath, Func<INativeTag> factory)
        {
            if (tagCache.TryGetValue(tagPath, out var cached))
                return cached.Value;

            return tagCache.GetOrAdd(tagPath, _ => new Lazy<INativeTag>(factory)).Value;
        }

        public void Flush()
        {
            foreach (var tag in tagCache.Values)
            {
                if (tag.IsValueCreated)
                    tag.Value.Dispose();
            }

            tagCache.Clear();
        }
    }
}
