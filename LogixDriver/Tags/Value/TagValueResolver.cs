using System.Buffers.Binary;
using System.Collections;
using System.Text;
using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    /// <summary>
    /// Base for value resolvers: primitive, string and array-element helpers. Subclasses decide
    /// what values look like (e.g. plain CLR objects, or a host application's value type).
    /// </summary>
    public abstract class TagValueResolverBase<T> : ITagValueResolver<T>
    {
        public Type ValueType => typeof(T);
        public abstract T ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0);
        public abstract void WriteTagBuffer(byte[] buffer, TypeRef type, T value, int offset = 0, int bitOffset = 0);

        object ITagValueResolver.ResolveValue(byte[] buffer, TypeRef type, int offset, int bitOffset)
            => ResolveValue(buffer, type, offset, bitOffset) ?? default!;
        void ITagValueResolver.WriteTagBuffer(byte[] buffer, TypeRef type, object value, int offset, int bitOffset)
            => WriteTagBuffer(buffer, type, (T)value, offset, bitOffset);

        /// <summary>
        /// The elements along an array's first dimension, with their positions. For a
        /// multi-dimensional array each element is itself an array (the remaining dimensions).
        /// BOOL array elements are one bit each.
        /// </summary>
        protected static IEnumerable<(TypeRef Type, int Offset, int BitOffset)> Elements(TypeRef array, int offset, int bitOffset = 0)
        {
            var element = array.Index(1);
            var elementBits = array.IsBitArray ? 1 : Resolved(array).Size * 8;
            var strideBits = elementBits * element.ElementCount;
            var start = offset * 8 + bitOffset;

            for (int i = 0; i < array.Dims[0]; i++)
            {
                var position = start + i * strideBits;
                yield return (element, position / 8, position % 8);
            }
        }

        protected static LogixType Resolved(TypeRef type) =>
            type.ElementType ?? throw new InvalidOperationException($"Type {type} hasn't been resolved.");

        protected static object ReadPrimitive(byte[] buffer, PrimitiveType type, int offset, int bitOffset = 0)
        {
            var span = buffer.AsSpan(offset);
            return type.Code switch
            {
                Code.BOOL => (buffer[offset + bitOffset / 8] & (1 << (bitOffset % 8))) != 0,
                Code.SINT => (sbyte)buffer[offset],
                Code.USINT => buffer[offset],
                Code.INT => BinaryPrimitives.ReadInt16LittleEndian(span),
                Code.UINT => BinaryPrimitives.ReadUInt16LittleEndian(span),
                Code.DINT or Code.TIME => BinaryPrimitives.ReadInt32LittleEndian(span),
                Code.UDINT => BinaryPrimitives.ReadUInt32LittleEndian(span),
                Code.LINT or Code.DATE_AND_TIME => BinaryPrimitives.ReadInt64LittleEndian(span),
                Code.ULINT => BinaryPrimitives.ReadUInt64LittleEndian(span),
                Code.REAL => BinaryPrimitives.ReadSingleLittleEndian(span),
                Code.LREAL => BinaryPrimitives.ReadDoubleLittleEndian(span),
                _ => throw new NotSupportedException($"Type {type.Name} can't be read.")
            };
        }

        // Convert (rather than unboxing) accepts any boxed numeric type, e.g. an int for a UINT;
        // out-of-range values throw OverflowException instead of silently wrapping.
        protected static void WritePrimitive(byte[] buffer, PrimitiveType type, object value, int offset, int bitOffset = 0)
        {
            var span = buffer.AsSpan(offset);
            switch (type.Code)
            {
                case Code.BOOL:
                    // bitOffset may run past the first byte, e.g. bit 8..31 of a BOOL array word
                    var index = offset + bitOffset / 8;
                    var mask = (byte)(1 << (bitOffset % 8));
                    buffer[index] = Convert.ToBoolean(value) ? (byte)(buffer[index] | mask) : (byte)(buffer[index] & ~mask);
                    break;
                case Code.SINT: buffer[offset] = unchecked((byte)Convert.ToSByte(value)); break;
                case Code.USINT: buffer[offset] = Convert.ToByte(value); break;
                case Code.INT: BinaryPrimitives.WriteInt16LittleEndian(span, Convert.ToInt16(value)); break;
                case Code.UINT: BinaryPrimitives.WriteUInt16LittleEndian(span, Convert.ToUInt16(value)); break;
                case Code.DINT or Code.TIME: BinaryPrimitives.WriteInt32LittleEndian(span, Convert.ToInt32(value)); break;
                case Code.UDINT: BinaryPrimitives.WriteUInt32LittleEndian(span, Convert.ToUInt32(value)); break;
                case Code.LINT or Code.DATE_AND_TIME: BinaryPrimitives.WriteInt64LittleEndian(span, Convert.ToInt64(value)); break;
                case Code.ULINT: BinaryPrimitives.WriteUInt64LittleEndian(span, Convert.ToUInt64(value)); break;
                case Code.REAL: BinaryPrimitives.WriteSingleLittleEndian(span, Convert.ToSingle(value)); break;
                case Code.LREAL: BinaryPrimitives.WriteDoubleLittleEndian(span, Convert.ToDouble(value)); break;
                default: throw new NotSupportedException($"Type {type.Name} can't be written.");
            }
        }

        protected static string ReadString(byte[] buffer, StringType type, int offset)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var available = Math.Max(0, Math.Min(type.Capacity, buffer.Length - offset - StringType.LengthBytes));
            return Encoding.Latin1.GetString(buffer, offset + StringType.LengthBytes, Math.Clamp(count, 0, available));
        }

        protected static void WriteString(byte[] buffer, StringType type, string value, int offset)
        {
            var data = buffer.AsSpan(offset + StringType.LengthBytes,
                Math.Max(0, Math.Min(type.Capacity, buffer.Length - offset - StringType.LengthBytes)));

            if (value.Length > data.Length)
                throw new ArgumentException($"String length {value.Length} exceeds {type.Name} capacity {data.Length}.");

            data.Clear();
            var written = Encoding.Latin1.GetBytes(value, data);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), written);
        }
    }

    /// <summary>
    /// Plain CLR values: primitives as their .NET types, strings as string, arrays as
    /// List&lt;object&gt; (nested per dimension), structures as Dictionary&lt;string, object&gt;.
    /// </summary>
    public class DefaultTagValueResolver : TagValueResolverBase<object>
    {
        public override object ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0)
        {
            if (type.IsArray)
                return Elements(type, offset, bitOffset)
                    .Select(e => ResolveValue(buffer, e.Type, e.Offset, e.BitOffset))
                    .ToList();

            return Resolved(type) switch
            {
                PrimitiveType primitive => ReadPrimitive(buffer, primitive, offset, bitOffset),
                StringType stringType => ReadString(buffer, stringType, offset),
                StructType structType => structType.Members.ToDictionary(
                    m => m.Name,
                    m => ResolveValue(buffer, m.Type, offset + m.Offset, m.BitOffset)),
                var other => throw new NotSupportedException($"Type {other.Name} can't be read.")
            };
        }

        public override void WriteTagBuffer(byte[] buffer, TypeRef type, object value, int offset = 0, int bitOffset = 0)
        {
            if (type.IsArray)
            {
                // any sequence works: arrays, List<object> (what ResolveValue returns), List<int>, etc.
                if (value is string || value is not IEnumerable sequence)
                    throw new ArgumentException($"Write value for {type} must be a sequence, got {value.GetType().Name}.");

                var items = sequence.Cast<object>().ToArray();
                if (items.Length != type.Dims[0])
                    throw new ArgumentException($"Write value for {type} has {items.Length} elements, expected {type.Dims[0]}.");

                var i = 0;
                foreach (var element in Elements(type, offset, bitOffset))
                    WriteTagBuffer(buffer, element.Type, items[i++], element.Offset, element.BitOffset);
                return;
            }

            switch (Resolved(type))
            {
                case PrimitiveType primitive:
                    WritePrimitive(buffer, primitive, value, offset, bitOffset);
                    break;
                case StringType stringType:
                    WriteString(buffer, stringType, value as string ?? throw new ArgumentException($"Write value for {stringType.Name} must be a string."), offset);
                    break;
                case StructType structType:
                    foreach (var m in structType.Members)
                    {
                        // every member is required; a skipped member would write back whatever was last read
                        if (!TryGetMemberValue(value, m.Name, out var memberValue))
                            throw new ArgumentException($"Write value for {structType.Name} is missing member '{m.Name}'.");
                        WriteTagBuffer(buffer, m.Type, memberValue!, offset + m.Offset, m.BitOffset);
                    }
                    break;
                case var other:
                    throw new NotSupportedException($"Type {other.Name} can't be written.");
            }
        }

        // accepts Dictionary<string, object> (what ResolveValue returns), ExpandoObject, or any other dictionary keyed by string
        private static bool TryGetMemberValue(object value, string name, out object? memberValue)
        {
            memberValue = null;
            if (value is IDictionary<string, object> generic)
                return generic.TryGetValue(name, out memberValue) && memberValue is not null;
            if (value is IDictionary nonGeneric)
                return nonGeneric.Contains(name) && (memberValue = nonGeneric[name]) is not null;

            throw new ArgumentException($"Write value for a structure must be a dictionary keyed by member name, got {value.GetType().Name}.");
        }
    }
}
