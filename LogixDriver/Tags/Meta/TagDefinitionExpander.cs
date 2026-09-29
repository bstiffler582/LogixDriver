using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    internal interface ITagDefinitionExpander
    {
        TagDefinition ExpandTagDefinition(TagDefinition root, bool deep = true);
        Task<TagDefinition> ExpandTagDefinitionAsync(TagDefinition root, bool deep = true);
    }

    /// <summary>
    /// Progressively builds a controller's tag tree based on what tags are requested.
    /// Defines TagDefinition parent->child relationships based on program/array/udt definitions.
    /// </summary>
    internal class TagDefinitionExpander : ITagDefinitionExpander
    {
        private readonly ITagValueReader reader;
        private readonly ITagMetaDecoder metaDecoder;
        private readonly ITagDefinitionCache tagCache;

        public TagDefinitionExpander(ITagValueReader reader, ITagDefinitionCache cache, ITagMetaDecoder? metaDecoder = null)
        {
            this.reader = reader;
            this.tagCache = cache;
            this.metaDecoder = metaDecoder ?? new TagMetaDecoder();
        }

        public TagDefinition ExpandTagDefinition(TagDefinition root, bool deep = true)
        {
            return ExpandTagDefinitionAsync(root, deep).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Resolves type and array definitions to populate child nodes.
        /// </summary>
        /// <param name="rootNode">Top level node to expand</param>
        /// <param name="deep">Recurse through nested members</param>
        /// <returns>The root node back</returns>
        public async Task<TagDefinition> ExpandTagDefinitionAsync(TagDefinition root, bool deep = true)
        {
            await ExpandInternal(root, deep).ConfigureAwait(false);
            return root;
        }

        private async Task ExpandInternal(TagDefinition tagDef, bool deep)
        {
            if (tagDef.ExpansionLevel == ExpansionLevel.Deep)
                return;

            if (tagDef.ExpansionLevel == ExpansionLevel.Shallow && !deep)
                return;

            if (IsArray(tagDef.TypeCode))
                await ExpandArray(tagDef, deep).ConfigureAwait(false);
            else if (IsUdt(tagDef.TypeCode))
                await ExpandUdt(tagDef, deep).ConfigureAwait(false);
            else if (tagDef.Name.StartsWith("Program:"))
                await ExpandProgram(tagDef, deep).ConfigureAwait(false);
            else
            {
                ExpandPrimitive(tagDef);
                tagDef.ExpansionLevel = ExpansionLevel.Deep;
                return;
            }

            tagDef.ExpansionLevel = deep ? ExpansionLevel.Deep : ExpansionLevel.Shallow;
        }

        private async Task ExpandProgram(TagDefinition tagDef, bool deep)
        {
            if (tagDef.ExpansionLevel == ExpansionLevel.None)
            {
                var programTagList = await reader.ReadRawAsync($"{tagDef.Name}.@tags").ConfigureAwait(false);
                var progTagInfos = metaDecoder.DecodeTagList(programTagList);

                var programTags = progTagInfos
                    .Where(t => !IsSystem(t.TypeCode))
                    .ToList();

                tagDef.Children = programTags;
            }

            if (deep)
            {
                foreach (var c in tagDef.Children!)
                    await ExpandInternal(c, true).ConfigureAwait(false);
            }
            
            tagDef.TypeName = tagDef.Name;
        }

        private async Task ExpandArray(TagDefinition tagDef, bool deep)
        {
            if (IsBoolArray(tagDef.TypeCode))
            {
                ExpandBoolArray(tagDef);
                return;
            }

            // Already built by a shallow pass: deepen the existing elements in place. Rebuilding would
            // swap in new nodes while the flat cache (first add wins) keeps serving the old ones.
            if (tagDef.ExpansionLevel == ExpansionLevel.Shallow)
            {
                if (deep)
                {
                    foreach (var element in tagDef.Children!)
                        await ExpandInternal(element, true).ConfigureAwait(false);
                }
                return;
            }

            var baseTypeCode = GetArrayBaseType(tagDef.TypeCode);
            var baseTag = new TagDefinition(tagDef) { TypeCode = baseTypeCode };
            await ExpandInternal(baseTag, deep).ConfigureAwait(false);

            var dims = tagDef.Dimensions?.Where(n => n > 0).ToArray();
            var arrayNode = BuildArrayType(tagDef, baseTag, dims, 0);

            tagDef.Length = arrayNode.Length;
            tagDef.TypeName = arrayNode.TypeName;
            tagDef.Dimensions = arrayNode.Dimensions;
            tagDef.Children = arrayNode.Children;
        }

        /// <summary>
        /// Logix reports BOOL[n] as an array of n/32 DWORDs. Re-shape it as n BOOL elements,
        /// each addressed by the byte offset of its containing word plus the bit within that word.
        /// </summary>
        private static void ExpandBoolArray(TagDefinition tagDef)
        {
            // already expanded; Dimensions are in bits now, so re-running would scale them again
            if (tagDef.Children is not null)
                return;

            var words = tagDef.Dimensions?.Where(n => n > 0).Aggregate(1u, (a, n) => a * n) ?? 0;
            var bits = (int)words * BOOL_ARRAY_WORD_BITS;
            const int wordBytes = BOOL_ARRAY_WORD_BITS / 8;

            tagDef.Length = words * wordBytes;
            tagDef.TypeName = $"ARRAY[{bits}] OF BOOL";
            tagDef.Dimensions = [(uint)bits];
            tagDef.Children = Enumerable.Range(0, bits)
                .Select(i => new TagDefinition(
                    $"{i}",
                    (ushort)Code.BOOL,
                    GetTypeLength((ushort)Code.BOOL),
                    (uint)(i / BOOL_ARRAY_WORD_BITS * wordBytes),
                    (uint)(i % BOOL_ARRAY_WORD_BITS),
                    ResolveTypeName((ushort)Code.BOOL))
                {
                    ExpansionLevel = ExpansionLevel.Deep
                })
                .ToList();
        }

        private TagDefinition BuildArrayType(TagDefinition rootTag, TagDefinition baseTag, uint[]? dims, int idx = 0)
        {
            if (dims is null || idx >= dims.Length || dims[idx] == 0)
                return baseTag;

            var child = BuildArrayType(rootTag, baseTag, dims, idx + 1);
            var dim = (int)dims[idx];
            var members = Enumerable.Range(0, dim)
                .Select(i => new TagDefinition(child)
                {
                    Name = $"{i}",
                    Offset = (uint)i * child.Length,
                    ExpansionLevel = baseTag.ExpansionLevel
                })
                .ToList();

            return new TagDefinition(
                child.Name,
                rootTag.TypeCode,
                (uint)dim * child.Length,
                rootTag.Offset,
                0,
                $"ARRAY[{dim}] OF {child.TypeName}",
                dims.Skip(idx).ToArray(),
                members
            );
        }

        private async Task ExpandUdt(TagDefinition tagDef, bool deep)
        {
            if (tagDef.ExpansionLevel == ExpansionLevel.None)
            {
                var udtId = GetUdtId(tagDef.TypeCode);

                TypeDefinition typeDef;
                if (tagCache.TryGetTypeDefinition(udtId, out var cached) && cached is not null)
                    typeDef = cached;
                else
                {
                    var template = await reader.ReadRawAsync($"@udt/{udtId}").ConfigureAwait(false);
                    typeDef = metaDecoder.DecodeUdtMeta(template);
                }

                var tagMembers = typeDef.Members?
                    .Except(BoolHostMembers(typeDef.Members))
                    .Select(TagDefinition.FromTypeMemberDefinition)
                    .ToList();

                if (deep)
                {
                    foreach (var member in tagMembers!)
                        await ExpandInternal(member, true).ConfigureAwait(false);
                }

                if (cached is null)
                    tagCache.AddTypeDefinition(udtId, typeDef);

                tagDef.Length = typeDef.Length;
                tagDef.TypeName = typeDef.Name;
                tagDef.Children = tagMembers;
            }

            if (deep && tagDef.ExpansionLevel == ExpansionLevel.Shallow)
            {
                foreach (var child in tagDef.Children!)
                    await ExpandInternal(child, true).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Logix packs UDT BOOL members into hidden SINT "host" members (named ZZZZZZZZZZ...) at the same
        /// byte offset. A host is storage for its BOOLs, not a value of its own: exposing it would let a
        /// write of the stale host byte overwrite the BOOLs sharing it, so hosts are left out of the tree.
        /// </summary>
        private static IEnumerable<TypeMemberDefinition> BoolHostMembers(List<TypeMemberDefinition> members)
        {
            var boolOffsets = members
                .Where(m => m.Code == (ushort)Code.BOOL)
                .Select(m => m.Offset)
                .ToHashSet();

            return members.Where(m => m.Code == (ushort)Code.SINT && boolOffsets.Contains(m.Offset));
        }

        private void ExpandPrimitive(TagDefinition tagDef)
        {
            var length = tagDef.Length > 0 ? tagDef.Length : GetTypeLength(tagDef.TypeCode);
            tagDef.Length = length;
            tagDef.TypeName = ResolveTypeName(tagDef.TypeCode);
            tagDef.Children = null;
        }
    }
}
