using System.Buffers.Binary;
using System.Text;
using static Logix.Tags.TagMetaHelpers;

namespace Logix.Tags
{
    public abstract class TagValueResolverBase<T> : ITagValueResolver<T>
    {
        // Logix STRING layout: DINT character count followed by up to 82 bytes of character data
        private const int StringCountBytes = 4;
        private const int StringCapacity = 82;

        public Type ValueType => typeof(T);
        public abstract T ResolveValue(byte[] buffer, TagDefinition definition, int offset = 0);
        public abstract void WriteTagBuffer(byte[] buffer, TagDefinition definition, T value, int offset = 0);
        object ITagValueResolver.ResolveValue(byte[] buffer, TagDefinition definition, int offset)
            => ResolveValue(buffer, definition, offset) ?? default!;
        void ITagValueResolver.WriteTagBuffer(byte[] buffer, TagDefinition definition, object value, int offset)
            => WriteTagBuffer(buffer, definition, (T)value, offset);

        // offset is a byte offset, except for BOOL where it is a bit offset
        protected object PrimitiveValueResolver(byte[] buffer, ushort typeCode, int offset = 0)
        {
            return (Code)(typeCode) switch
            {
                Code.BOOL => GetBit(buffer, offset),
                Code.SINT => (sbyte)buffer[offset],
                Code.USINT or Code.BYTE => buffer[offset],
                Code.INT => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset)),
                Code.UINT or Code.WORD => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset)),
                Code.DINT => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)),
                Code.UDINT or Code.DWORD => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)),
                Code.LINT => BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset)),
                Code.ULINT or Code.LWORD => BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset)),
                Code.REAL => BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset)),
                Code.LREAL => BinaryPrimitives.ReadDoubleLittleEndian(buffer.AsSpan(offset)),
                Code.STRING or Code.STRING2 or Code.STRINGI or Code.STRINGN or Code.STRING_STRUCT
                    => GetString(buffer, offset),
                _ => throw new Exception($"Primitive type code:{typeCode:X} not handled")
            };
        }

        // Convert (rather than unboxing) accepts any boxed numeric type, e.g. an int for a UINT;
        // out-of-range values throw OverflowException instead of silently wrapping.
        protected void PrimitiveValueWriter(byte[] buffer, ushort typeCode, object value, int offset = 0)
        {
            switch ((Code)typeCode)
            {
                case Code.BOOL:
                    SetBit(buffer, offset, Convert.ToBoolean(value));
                    break;
                case Code.SINT:
                    buffer[offset] = unchecked((byte)Convert.ToSByte(value));
                    break;
                case Code.USINT or Code.BYTE:
                    buffer[offset] = Convert.ToByte(value);
                    break;
                case Code.INT:
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset), Convert.ToInt16(value));
                    break;
                case Code.UINT or Code.WORD:
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), Convert.ToUInt16(value));
                    break;
                case Code.DINT:
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), Convert.ToInt32(value));
                    break;
                case Code.UDINT or Code.DWORD:
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Convert.ToUInt32(value));
                    break;
                case Code.LINT:
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), Convert.ToInt64(value));
                    break;
                case Code.ULINT or Code.LWORD:
                    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), Convert.ToUInt64(value));
                    break;
                case Code.REAL:
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), Convert.ToSingle(value));
                    break;
                case Code.LREAL:
                    BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset), Convert.ToDouble(value));
                    break;
                case Code.STRING or Code.STRING2 or Code.STRINGI or Code.STRINGN or Code.STRING_STRUCT:
                    SetString(buffer, offset, (string)value);
                    break;
                default:
                    throw new Exception($"No primitive type resolver for TypeCode {typeCode}");
            }
        }

        private static bool GetBit(byte[] buffer, int bitOffset)
            => (buffer[bitOffset >> 3] & (1 << (bitOffset & 7))) != 0;

        private static void SetBit(byte[] buffer, int bitOffset, bool value)
        {
            var mask = (byte)(1 << (bitOffset & 7));
            if (value)
                buffer[bitOffset >> 3] |= mask;
            else
                buffer[bitOffset >> 3] &= (byte)~mask;
        }

        private static string GetString(byte[] buffer, int offset)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var available = Math.Max(0, Math.Min(StringCapacity, buffer.Length - offset - StringCountBytes));
            return Encoding.Latin1.GetString(buffer, offset + StringCountBytes, Math.Clamp(count, 0, available));
        }

        private static void SetString(byte[] buffer, int offset, string value)
        {
            var data = buffer.AsSpan(offset + StringCountBytes,
                Math.Max(0, Math.Min(StringCapacity, buffer.Length - offset - StringCountBytes)));

            if (value.Length > data.Length)
                throw new ArgumentException($"String length {value.Length} exceeds capacity {data.Length}.");

            data.Clear();
            var written = Encoding.Latin1.GetBytes(value, data);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), written);
        }
    }

    public class DefaultTagValueResolver : TagValueResolverBase<object>
    {
        public override object ResolveValue(byte[] buffer, TagDefinition definition, int offset = 0)
        {
            if (IsArray(definition.TypeCode))
            {
                if (definition.Children is null || definition.Children.Count < 1)
                    return 0;

                var ret = new List<object>();
                foreach (var m in definition.Children)
                    ret.Add(ResolveValue(buffer, m, offset + (int)m.Offset));

                return ret;
            }
            else if (IsUdt(definition.TypeCode) && !definition.TypeName.Contains("STRING"))
            {
                if (definition.Children is null || definition.Children.Count < 1)
                    return 0;

                var ret = new Dictionary<string, object>();
                foreach (var c in definition.Children)
                {
                    if (c.TypeCode == (ushort)Code.BOOL)
                        ret[c.Name] = ResolveValue(buffer, c, ((offset + (int)c.Offset) * 8) + (int)c.BitOffset);
                    else
                        ret[c.Name] = ResolveValue(buffer, c, offset + (int)c.Offset);
                }

                return ret;
            }
            else
            {
                return PrimitiveValueResolver(buffer, definition.TypeCode, offset);
            }
        }

        public override void WriteTagBuffer(byte[] buffer, TagDefinition definition, object value, int offset = 0)
        {
            if (IsArray(definition.TypeCode))
            {
                if (definition.Children is null || definition.Children.Count < 1)
                    return;

                // cast object value to array
                object[]? arr = null;
                if (value.GetType() == typeof(IEnumerable<>))
                    arr = (value as IEnumerable<object>)?.ToArray();
                else if (value is Array genericArray)
                    arr = genericArray.Cast<object>().ToArray();

                if (arr is null) throw new Exception($"Unable to cast write value for array tag {definition.Name} to Enumerable.");

                foreach (var c in definition.Children)
                {
                    {
                        int.TryParse(c.Name, out var i);
                        WriteTagBuffer(buffer, c, arr[i], offset + (int)c.Offset);
                    }
                }
            }
            else if (IsUdt(definition.TypeCode) && !definition.TypeName.Contains("STRING"))
            {
                if (definition.Children is null || definition.Children.Count < 1)
                    return;

                if (value.GetType() == typeof(IDictionary<string, object>))
                {
                    var dict = value as IDictionary<string, object>;
                    if (dict is null) throw new Exception($"Unable to cast write value for tag {definition.Name} to Dictionary.");
                    foreach (var c in definition.Children)
                    {
                        if (c.TypeCode == (ushort)Code.BOOL)
                            WriteTagBuffer(buffer, c, dict[c.Name], ((offset + (int)c.Offset) * 8) + (int)c.BitOffset);
                        else
                            WriteTagBuffer(buffer, c, dict[c.Name], offset + (int)c.Offset);
                    }
                }
            }
            else
            {
                PrimitiveValueWriter(buffer, definition.TypeCode, value, offset);
            }
        }
    }
}
