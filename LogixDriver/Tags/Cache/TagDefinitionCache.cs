using System.Collections.Concurrent;

namespace Logix.Tags
{
    public interface ITagDefinitionCache
    {
        public void AddTagDefinition(TagDefinition tagDefinition);
        public bool TryGetTagDefinition(string tagPath, out TagDefinition? tagDefinition);
        public IEnumerable<TagDefinition> GetTagDefinitions();
        public IReadOnlyDictionary<string, TagDefinition> GetTagDefinitionsFlat();
        public void AddTypeDefinition(ushort code, TypeDefinition typeDefinition);
        public bool TryGetTypeDefinition(ushort code, out TypeDefinition? typeDefinition);
    }

    internal class TagDefinitionCache : ITagDefinitionCache
    {
        // Root definitions keep insertion order (drives tag browser order), so guard with a lock
        // and hand out snapshots. Flat and type maps are read on every tag access -> concurrent.
        private readonly object rootsLock = new();
        private readonly Dictionary<string, TagDefinition> tagDefinitions = new();
        private readonly ConcurrentDictionary<string, TagDefinition> tagDefinitionsFlat = new();
        private readonly ConcurrentDictionary<ushort, TypeDefinition> typeDefinitionCache = new();

        public void AddTagDefinition(TagDefinition tagDefinition)
        {
            lock (rootsLock)
                tagDefinitions.TryAdd(tagDefinition.Name, tagDefinition);

            var flattened = Flatten(tagDefinition);
            foreach (var t in flattened)
                tagDefinitionsFlat.TryAdd(t.Path, t.TagDef);
        }

        public IEnumerable<TagDefinition> GetTagDefinitions()
        {
            lock (rootsLock)
                return tagDefinitions.Values.ToList();
        }

        public IReadOnlyDictionary<string, TagDefinition> GetTagDefinitionsFlat()
        {
            return tagDefinitionsFlat;
        }

        public bool TryGetTagDefinition(string tagName, out TagDefinition? tagDefinition)
        {
            return tagDefinitionsFlat.TryGetValue(tagName, out tagDefinition);
        }

        public void AddTypeDefinition(ushort code, TypeDefinition typeDefinition)
        {
            typeDefinitionCache.TryAdd(code, typeDefinition);
        }

        public bool TryGetTypeDefinition(ushort code, out TypeDefinition? typeDefinition)
        {
            return typeDefinitionCache.TryGetValue(code, out typeDefinition);
        }

        private IEnumerable<(string Path, TagDefinition TagDef)> Flatten(TagDefinition node, string parentPath = "")
        {
            string fullPath;
            if (int.TryParse(node.Name, out var idx) && !string.IsNullOrEmpty(parentPath))
                fullPath = $"{parentPath}[{idx}]";
            else
                fullPath = string.IsNullOrEmpty(parentPath) ? node.Name : $"{parentPath}.{node.Name}";

            yield return (fullPath, node);

            if (node.TypeName.Contains("STRING"))
                yield break;

            if (node.Children is not null)
            {
                foreach (var child in node.Children.SelectMany(c => Flatten(c, fullPath)))
                    yield return child;
            }
        }
    }
}
