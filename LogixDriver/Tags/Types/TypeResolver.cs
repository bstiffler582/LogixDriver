using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    /// <summary>
    /// Builds structure types from their templates, one controller read per template id, shared by
    /// every tag that uses the type. Resolution is lazy: <see cref="ResolveAsync"/> fetches one
    /// type, <see cref="ResolveClosureAsync"/> fetches everything a type contains.
    /// </summary>
    internal sealed class TypeResolver
    {
        private readonly IRawTagReader reader;
        private readonly ITagMetaDecoder decoder;
        private readonly AsyncCache<ushort, LogixType> structs = new();
        private readonly AsyncCache<ushort, bool> closures = new();

        public TypeResolver(IRawTagReader reader, ITagMetaDecoder? decoder = null)
        {
            this.reader = reader;
            this.decoder = decoder ?? new TagMetaDecoder();
        }

        /// <summary>Resolves the element type of <paramref name="type"/>, fetching its template if needed.</summary>
        public async Task<LogixType> ResolveAsync(TypeRef type)
        {
            if (type.ElementType is { } resolved)
                return resolved;

            type.Resolve(await structs.GetOrAddAsync(GetUdtId(type.Code), FetchAsync).ConfigureAwait(false));
            return type.ElementType!;
        }

        /// <summary>Resolves the element type and, recursively, the types of all its members.</summary>
        public async Task ResolveClosureAsync(TypeRef type)
        {
            if (await ResolveAsync(type).ConfigureAwait(false) is not StructType structType || structType.IsClosureResolved)
                return;

            await closures.GetOrAddAsync(structType.Id, async _ =>
            {
                await Task.WhenAll(structType.Members.Select(m => ResolveClosureAsync(m.Type))).ConfigureAwait(false);
                structType.MarkClosureResolved();
                return true;
            }).ConfigureAwait(false);
        }

        /// <summary>A structure type, if its template has already been read.</summary>
        public bool TryGet(ushort templateId, out LogixType type) => structs.TryGet(templateId, out type);

        public void Clear()
        {
            structs.Clear();
            closures.Clear();
        }

        private async Task<LogixType> FetchAsync(ushort templateId)
        {
            var data = await reader.ReadRawAsync($"@udt/{templateId}").ConfigureAwait(false);
            return Build(decoder.DecodeUdtMeta(data));
        }

        internal static LogixType Build(UdtTemplate template)
        {
            // Logix packs BOOL members into hidden SINT "host" members (ZZZZZZZZZZ...) at the same
            // offset. A host is storage for its BOOLs, not a value: exposing it would let a write of
            // the stale host byte overwrite the BOOLs sharing it.
            var boolOffsets = template.Members
                .Where(m => m.Type == (ushort)Code.BOOL)
                .Select(m => m.Offset)
                .ToHashSet();

            var members = template.Members
                .Where(m => !(m.Type == (ushort)Code.SINT && boolOffsets.Contains(m.Offset)))
                .Select(m => new Member(
                    m.Name,
                    TypeRef.FromRaw(m.Type, IsArray(m.Type) ? new uint[] { m.Info } : Array.Empty<uint>()),
                    (int)m.Offset,
                    m.Type == (ushort)Code.BOOL ? m.Info : 0))
                .ToList();

            return IsStringLayout(members)
                ? new StringType(template.Id, template.Name, (int)template.Size, members[1].Type.Dims[0])
                : new StructType(template.Id, template.Name, (int)template.Size, members);
        }

        // STRING and user-defined string types: DINT LEN followed by SINT[n] DATA
        private static bool IsStringLayout(IReadOnlyList<Member> members) =>
            members.Count == 2
            && members[0].Name.Equals("LEN", StringComparison.OrdinalIgnoreCase)
            && members[0].Type is { Code: (ushort)Code.DINT, IsArray: false }
            && members[1].Name.Equals("DATA", StringComparison.OrdinalIgnoreCase)
            && members[1].Type is { Code: (ushort)Code.SINT, Dims.Count: 1 };
    }
}
