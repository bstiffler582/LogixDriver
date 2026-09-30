using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    /// <summary>
    /// A resolved Logix data type. Types are shared by every tag and member that uses them and
    /// never change once built; per-instance data (names, array shapes) lives on <see cref="TypeRef"/>.
    /// </summary>
    public abstract record LogixType(string Name, int Size);

    /// <summary>An atomic type: BOOL, SINT, DINT, REAL, ... <see cref="LogixType.Size"/> is in bytes.</summary>
    public sealed record PrimitiveType(Code Code, string Name, int Size) : LogixType(Name, Size)
    {
        private static readonly Dictionary<ushort, PrimitiveType> Known = new[]
        {
            new PrimitiveType(Code.BOOL, "BOOL", 1),
            new PrimitiveType(Code.SINT, "SINT", 1), new PrimitiveType(Code.USINT, "USINT", 1),
            new PrimitiveType(Code.INT, "INT", 2), new PrimitiveType(Code.UINT, "UINT", 2),
            new PrimitiveType(Code.DINT, "DINT", 4), new PrimitiveType(Code.UDINT, "UDINT", 4),
            new PrimitiveType(Code.LINT, "LINT", 8), new PrimitiveType(Code.ULINT, "ULINT", 8),
            new PrimitiveType(Code.REAL, "REAL", 4), new PrimitiveType(Code.LREAL, "LREAL", 8),
            new PrimitiveType(Code.TIME, "TIME", 4), new PrimitiveType(Code.DATE_AND_TIME, "DATE_AND_TIME", 8),
        }.ToDictionary(p => (ushort)p.Code);

        public static readonly PrimitiveType Bool = Known[(ushort)Code.BOOL];

        /// <summary>Known atomic type for the code, or a size-0 placeholder that can't be read or written.</summary>
        public static PrimitiveType For(ushort code) =>
            Known.TryGetValue(code, out var type) ? type : new PrimitiveType((Code)code, ResolveTypeName(code), 0);
    }

    /// <summary>
    /// A Logix string: a structure of a DINT LEN followed by a SINT[Capacity] DATA. STRING is the
    /// built-in one (capacity 82); user-defined string types differ only in capacity.
    /// </summary>
    public sealed record StringType(ushort Id, string Name, int Size, int Capacity) : LogixType(Name, Size)
    {
        public const int LengthBytes = 4;
    }

    /// <summary>A UDT or predefined structure (TIMER, COUNTER, ...), built from its template.</summary>
    public sealed record StructType : LogixType
    {
        private readonly Dictionary<string, Member> byName;
        private volatile bool closureResolved;

        public ushort Id { get; }
        public IReadOnlyList<Member> Members { get; }

        public StructType(ushort id, string name, int size, IReadOnlyList<Member> members) : base(name, size)
        {
            Id = id;
            Members = members;
            byName = members.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Member by name; Logix names are case-insensitive.</summary>
        public Member? GetMember(string name) => byName.GetValueOrDefault(name);

        /// <summary>True once every member type, all the way down, has been resolved.</summary>
        public bool IsClosureResolved => closureResolved;
        internal void MarkClosureResolved() => closureResolved = true;
    }

    /// <param name="BitOffset">bit within the byte at <paramref name="Offset"/>; only non-zero for BOOL members</param>
    public sealed record Member(string Name, TypeRef Type, int Offset, int BitOffset);

    /// <summary>
    /// The type of a tag or member: an element type plus array dimensions (empty for scalars).
    /// <see cref="ElementType"/> is null until the element type has been resolved; primitives are
    /// resolved from the start, structures once their template has been read.
    /// </summary>
    public sealed class TypeRef
    {
        private LogixType? elementType;

        /// <summary>Element type code, without array flags: an atomic code or 0x8000 | template id.</summary>
        public ushort Code { get; }
        public IReadOnlyList<int> Dims { get; }
        public LogixType? ElementType => Volatile.Read(ref elementType);

        public bool IsResolved => ElementType is not null;
        public bool IsArray => Dims.Count > 0;
        public bool IsStruct => IsUdt(Code);
        /// <summary>A BOOL array. Logix packs these into 32-bit words, one bit per element.</summary>
        public bool IsBitArray => IsArray && Code == (ushort)TagMetaHelpers.Code.BOOL;
        public int ElementCount => Dims.Aggregate(1, (n, d) => n * d);

        private TypeRef(ushort code, IReadOnlyList<int> dims, LogixType? elementType)
        {
            Code = code;
            Dims = dims;
            this.elementType = elementType ?? (IsUdt(code) ? null : PrimitiveType.For(code));
        }

        /// <summary>
        /// From a raw type code as reported by @tags or a template. BOOL arrays are reported as arrays
        /// of 0xD3 (a 32-bit word) with dimensions counted in words; they're normalized to BOOL
        /// arrays counted in bits.
        /// </summary>
        public static TypeRef FromRaw(ushort rawCode, IEnumerable<uint> dims)
        {
            if (!TagMetaHelpers.IsArray(rawCode))
                return new TypeRef(rawCode, Array.Empty<int>(), null);

            var code = GetArrayBaseType(rawCode);
            var shape = dims.Where(d => d > 0).Select(d => (int)d).ToArray();

            if (code == BOOL_ARRAY_BASE_TYPE)
                return new TypeRef((ushort)TagMetaHelpers.Code.BOOL, new[] { shape.Aggregate(1, (n, d) => n * d) * BOOL_ARRAY_WORD_BITS }, null);

            return new TypeRef(code, shape, null);
        }

        /// <summary>The same element type with some leading dimensions indexed away.</summary>
        public TypeRef Index(int indexedDims) =>
            new(Code, Dims.Skip(indexedDims).ToArray(), ElementType);

        internal void Resolve(LogixType type) => Interlocked.CompareExchange(ref elementType, type, null);

        public override string ToString()
        {
            var name = ElementType?.Name ?? ResolveTypeName(Code);
            return IsArray ? $"{name}[{string.Join(",", Dims)}]" : name;
        }
    }
}
