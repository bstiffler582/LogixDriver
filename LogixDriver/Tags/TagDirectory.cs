using System.Text;
using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    /// <summary>A top-level node of the controller: a controller tag or a program.</summary>
    /// <param name="Path">full path, e.g. "Recipes", "Program:Main" or "Program:Main.Recipes"</param>
    public abstract record TagNode(string Name, string Path);

    /// <summary>A controller- or program-scoped tag, as listed by the controller.</summary>
    /// <param name="Type">walk <see cref="TypeRef.ElementType"/> for members; it's null until that type is loaded</param>
    public sealed record TagInfo(string Name, string Path, TypeRef Type) : TagNode(Name, Path);

    /// <summary>A program. <see cref="Tags"/> is null until the program's tag list has been read.</summary>
    public sealed record ProgramInfo(string Name, IReadOnlyList<TagInfo>? Tags) : TagNode(Name, Name)
    {
        public bool IsLoaded => Tags is not null;
    }

    /// <summary>
    /// Where a tag path lives on the controller and what reading it returns.
    /// </summary>
    /// <param name="NativeName">name to request from the controller, in Logix syntax (e.g. Grid[1,0])</param>
    /// <param name="ElementCount">elements to request: array elements, or 32-bit words for BOOL arrays</param>
    /// <param name="Type">what the read returns; its closure is resolved</param>
    /// <param name="BitOffset">for a BOOL array element, its bit within the word that is read</param>
    /// <param name="IsBitArrayElement">writes change one bit of a word, so they must read-modify-write it</param>
    public sealed record ResolvedTag(string NativeName, int ElementCount, TypeRef Type, int BitOffset = 0, bool IsBitArrayElement = false);

    public interface ITagDirectory
    {
        /// <summary>
        /// Reads the controller tag list and fully resolves the types of the given paths, for browsing.
        /// A program name ("Program:Main") loads all of that program's tags. No paths (null or empty)
        /// resolves everything.
        /// </summary>
        Task LoadAsync(IEnumerable<string>? paths = null);

        /// <summary>
        /// Resolves a tag path such as "Program:Main.Recipes[3].Temp", reading only the metadata it
        /// needs: the tag lists involved, the templates along the path, and the target's own types.
        /// Results are cached per path.
        /// </summary>
        Task<ResolvedTag> ResolveAsync(string path);

        /// <summary>
        /// The controller's top level as loaded so far, in controller order: controller tags
        /// (<see cref="TagInfo"/>) and programs (<see cref="ProgramInfo"/>, with their tags once
        /// loaded by a filter or by resolving a path in them). Empty until the tag list has been read.
        /// Types below each tag are loaded where <see cref="TypeRef.IsResolved"/> is true.
        /// </summary>
        IReadOnlyList<TagNode> GetLoadedTags();

        /// <summary>
        /// Forgets everything read so far (tag lists, types, resolved paths), so the next load or
        /// resolve reads it again, e.g. after the controller program has been changed.
        /// </summary>
        void Refresh();
    }

    internal sealed class TagDirectory : ITagDirectory
    {
        private const string ControllerScope = "@tags";
        private const string ProgramPrefix = "Program:";

        private readonly IRawTagReader reader;
        private readonly ITagMetaDecoder decoder;
        private readonly TypeResolver types;
        private readonly AsyncCache<string, TagList> tagLists = new(StringComparer.OrdinalIgnoreCase);
        private readonly AsyncCache<string, ResolvedTag> resolved = new(StringComparer.OrdinalIgnoreCase);

        public TagDirectory(IRawTagReader reader, ITagMetaDecoder? decoder = null)
        {
            this.reader = reader;
            this.decoder = decoder ?? new TagMetaDecoder();
            types = new TypeResolver(reader, this.decoder);
        }

        public async Task LoadAsync(IEnumerable<string>? paths = null)
        {
            var controller = await GetTagListAsync(ControllerScope).ConfigureAwait(false);

            // an empty filter (e.g. an unset selector in configuration) means no filter
            var filter = paths?.ToList();
            var loads = filter is null || filter.Count == 0
                ? controller.Tags.Select(t => types.ResolveClosureAsync(t.Type))
                    .Concat(controller.Programs.Select(LoadProgramAsync))
                : filter.Select(path => IsProgramName(path) ? LoadProgramAsync(path) : ResolveAsync(path));

            await Task.WhenAll(loads).ConfigureAwait(false);
        }

        public Task<ResolvedTag> ResolveAsync(string path) => resolved.GetOrAddAsync(path, ResolvePathAsync);

        public IReadOnlyList<TagNode> GetLoadedTags()
        {
            if (!tagLists.TryGet(ControllerScope, out var controller))
                return Array.Empty<TagNode>();

            return controller.Nodes
                .Select(node => node is ProgramInfo program && tagLists.TryGet(ProgramScope(program.Name), out var list)
                    ? program with { Tags = list.Tags }
                    : node)
                .ToList();
        }

        public void Refresh()
        {
            tagLists.Clear();
            resolved.Clear();
            types.Clear();
        }

        private async Task LoadProgramAsync(string program)
        {
            var list = await GetProgramTagListAsync(program).ConfigureAwait(false);
            await Task.WhenAll(list.Tags.Select(t => types.ResolveClosureAsync(t.Type))).ConfigureAwait(false);
        }

        private async Task<ResolvedTag> ResolvePathAsync(string path)
        {
            var segments = TagPath.Parse(path);

            // root: a controller tag, or "Program:X" followed by one of its tags
            TagInfo root;
            string nativeName;
            int next;
            if (IsProgramName(segments[0].Member))
            {
                if (segments.Count < 2 || segments[1].Member is null)
                    throw new ArgumentException($"'{path}' names a program, not a tag.");

                var programs = await GetProgramTagListAsync(segments[0].Member!).ConfigureAwait(false);
                root = programs.Find(segments[1].Member!, path);
                nativeName = $"{programs.Scope}.{root.Name}";
                next = 2;
            }
            else
            {
                var controller = await GetTagListAsync(ControllerScope).ConfigureAwait(false);
                root = controller.Find(segments[0].Member ?? throw new ArgumentException($"'{path}' doesn't start with a tag name."), path);
                nativeName = root.Name;
                next = 1;
            }

            var native = new StringBuilder(nativeName);
            var type = root.Type;

            for (int i = next; i < segments.Count; i++)
            {
                var segment = segments[i];
                var isLast = i == segments.Count - 1;

                if (segment.Member is { } memberName)
                {
                    if (type.IsArray)
                        throw new ArgumentException($"'{path}': '{memberName}' is a member access on an array; index it first.");
                    if (await types.ResolveAsync(type).ConfigureAwait(false) is not StructType structType)
                        throw new KeyNotFoundException($"'{path}': {type} has no members.");

                    var member = structType.GetMember(memberName)
                        ?? throw new KeyNotFoundException($"'{path}': {structType.Name} has no member '{memberName}'.");
                    native.Append('.').Append(member.Name);
                    type = member.Type;
                    continue;
                }

                var indices = segment.Indices!;
                if (!type.IsArray)
                    throw new ArgumentException($"'{path}': {type} is not an array.");
                if (indices.Length > type.Dims.Count)
                    throw new ArgumentException($"'{path}': {indices.Length} indices for a {type.Dims.Count}-dimensional array.");
                for (int d = 0; d < indices.Length; d++)
                    if (indices[d] < 0 || indices[d] >= type.Dims[d])
                        throw new KeyNotFoundException($"'{path}': index {indices[d]} is outside 0..{type.Dims[d] - 1}.");

                if (type.IsBitArray)
                {
                    // Logix indexes BOOL arrays by 32-bit word: read the word holding the bit
                    if (!isLast)
                        throw new ArgumentException($"'{path}': a BOOL array element has no members.");
                    native.Append('[').Append(indices[0] / BOOL_ARRAY_WORD_BITS).Append(']');
                    return new ResolvedTag(native.ToString(), 1, type.Index(1), indices[0] % BOOL_ARRAY_WORD_BITS, IsBitArrayElement: true);
                }

                if (indices.Length < type.Dims.Count)
                {
                    // a partial index selects a sub-array: read it starting from its first element
                    if (!isLast)
                        throw new ArgumentException($"'{path}': '{segment}' selects a sub-array; index all {type.Dims.Count} dimensions to go further.");
                    var subArray = type.Index(indices.Length);
                    AppendIndices(native, indices.Concat(subArray.Dims.Select(_ => 0)));
                    return await FinishAsync(native.ToString(), subArray, subArray.ElementCount).ConfigureAwait(false);
                }

                AppendIndices(native, indices);
                type = type.Index(indices.Length);
            }

            if (!type.IsArray)
                return await FinishAsync(native.ToString(), type, 1).ConfigureAwait(false);

            // a whole array: read it starting from its first element
            AppendIndices(native, type.Dims.Select(_ => 0));
            var count = type.IsBitArray ? type.ElementCount / BOOL_ARRAY_WORD_BITS : type.ElementCount;
            return await FinishAsync(native.ToString(), type, count).ConfigureAwait(false);
        }

        private async Task<ResolvedTag> FinishAsync(string nativeName, TypeRef type, int elementCount)
        {
            // reading and writing a value needs every type it contains
            await types.ResolveClosureAsync(type).ConfigureAwait(false);
            return new ResolvedTag(nativeName, elementCount, type);
        }

        private static void AppendIndices(StringBuilder native, IEnumerable<int> indices) =>
            native.Append('[').AppendJoin(',', indices).Append(']');

        private async Task<TagList> GetProgramTagListAsync(string program)
        {
            // canonical casing from the controller list, so native names match what the controller reports
            var controller = await GetTagListAsync(ControllerScope).ConfigureAwait(false);
            var canonical = controller.Programs.FirstOrDefault(p => p.Equals(program, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No program '{program}'.");
            return await GetTagListAsync(ProgramScope(canonical)).ConfigureAwait(false);
        }

        private Task<TagList> GetTagListAsync(string scope) => tagLists.GetOrAddAsync(scope, async s =>
        {
            var entries = decoder.DecodeTagList(await reader.ReadRawAsync(s).ConfigureAwait(false));
            return new TagList(s == ControllerScope ? "" : s[..^(ControllerScope.Length + 1)], entries);
        });

        private static string ProgramScope(string program) => $"{program}.{ControllerScope}";

        private static bool IsProgramName(string? name) =>
            name is not null && name.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase)
            && name.IndexOfAny(new[] { '.', '[' }) < 0;

        /// <summary>One scope's tags: the controller (with its programs) or a single program.</summary>
        private sealed class TagList
        {
            private readonly Dictionary<string, TagInfo> byName;

            public string Scope { get; }
            /// <summary>Tags and programs (unloaded) in controller order.</summary>
            public IReadOnlyList<TagNode> Nodes { get; }
            public IReadOnlyList<TagInfo> Tags { get; }
            public IReadOnlyList<string> Programs { get; }

            public TagList(string scope, IReadOnlyList<TagListEntry> entries)
            {
                Scope = scope;
                Nodes = entries
                    .Select(e => e.Name.StartsWith(ProgramPrefix, StringComparison.OrdinalIgnoreCase)
                        ? new ProgramInfo(e.Name, null)
                        : IsSystem(e.Type)
                            ? null
                            : (TagNode)new TagInfo(e.Name, scope == "" ? e.Name : $"{scope}.{e.Name}", TypeRef.FromRaw(e.Type, e.Dims)))
                    .OfType<TagNode>()
                    .ToList();
                Tags = Nodes.OfType<TagInfo>().ToList();
                Programs = Nodes.OfType<ProgramInfo>().Select(p => p.Name).ToList();
                byName = Tags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            }

            public TagInfo Find(string name, string path) =>
                byName.GetValueOrDefault(name)
                ?? throw new KeyNotFoundException($"'{path}': no tag '{name}'{(Scope == "" ? "" : $" in {Scope}")}.");
        }
    }

    /// <summary>A parsed tag path: member names and index groups, e.g. Recipes[3].Temp or Grid[1,2].</summary>
    internal static class TagPath
    {
        /// <summary>A member name, or the indices of one index group ("[1,2]" and "[1][2]" are equivalent).</summary>
        public readonly record struct Segment(string? Member, int[]? Indices)
        {
            public override string ToString() => Member ?? $"[{string.Join(",", Indices!)}]";
        }

        public static List<Segment> Parse(string path)
        {
            var segments = new List<Segment>();
            int i = 0;
            while (i < path.Length)
            {
                if (path[i] == '[')
                {
                    var close = path.IndexOf(']', i);
                    if (close < 0)
                        throw new ArgumentException($"'{path}': unclosed '['.");

                    var indices = path[(i + 1)..close].Split(',').Select(s => int.TryParse(s.Trim(), out var n)
                        ? n : throw new ArgumentException($"'{path}': '{s}' is not an index.")).ToArray();

                    // [1][2] continues the previous group, the same as [1,2]
                    if (segments.Count > 0 && segments[^1].Indices is { } previous && path[i - 1] == ']')
                        segments[^1] = new Segment(null, previous.Concat(indices).ToArray());
                    else
                        segments.Add(new Segment(null, indices));
                    i = close + 1;
                }
                else
                {
                    if (path[i] == '.')
                    {
                        if (segments.Count == 0)
                            throw new ArgumentException($"'{path}' starts with '.'.");
                        i++;
                    }
                    var end = path.IndexOfAny(new[] { '.', '[' }, i);
                    if (end < 0) end = path.Length;
                    if (end == i)
                        throw new ArgumentException($"'{path}': empty member name.");
                    segments.Add(new Segment(path[i..end], null));
                    i = end;
                }
            }

            if (segments.Count == 0 || segments[0].Member is null)
                throw new ArgumentException($"'{path}' doesn't start with a tag name.");
            return segments;
        }
    }
}
