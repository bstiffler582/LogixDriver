using System.Buffers.Binary;
using System.Text;

namespace Logix.Tags
{
    /// <summary>One entry of an @tags (or Program:X.@tags) response.</summary>
    internal record TagListEntry(string Name, ushort Type, uint[] Dims);

    /// <summary>A decoded @udt/{id} template, before any interpretation.</summary>
    internal record UdtTemplate(ushort Id, string Name, uint Size, IReadOnlyList<UdtTemplateMember> Members);

    /// <param name="Info">bit number for BOOL members, element count for array members</param>
    internal record UdtTemplateMember(string Name, ushort Type, uint Offset, ushort Info);

    internal interface ITagMetaDecoder
    {
        IReadOnlyList<TagListEntry> DecodeTagList(byte[] data);
        UdtTemplate DecodeUdtMeta(byte[] data);
    }

    internal class TagMetaDecoder : ITagMetaDecoder
    {
        // @tags entry: instance id (4), type (2), element length (2), 3 dimensions (4 each),
        // name length (2), then the name bytes
        private const int TagEntryHeaderSize = 22;
        private const int MaxTagNameLength = 399;

        public IReadOnlyList<TagListEntry> DecodeTagList(byte[] data)
        {
            var tagList = new List<TagListEntry>();
            var span = data.AsSpan();

            int offset = 0;
            while (offset + TagEntryHeaderSize <= span.Length)
            {
                var entry = span[offset..];
                var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
                var dims = new uint[]
                {
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(entry[16..])
                };
                var nameLength = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]), MaxTagNameLength);
                nameLength = Math.Min(nameLength, entry.Length - TagEntryHeaderSize);
                var name = Encoding.ASCII.GetString(entry.Slice(TagEntryHeaderSize, nameLength));

                tagList.Add(new TagListEntry(name, type, dims));
                offset += TagEntryHeaderSize + nameLength;
            }

            return tagList;
        }

        // Template layout: id (2), member description size (4), instance size (4), member count (2),
        // handle (2), then 8 bytes per member (info (2), type (2), offset (4)), then the template
        // name ("Name;..."), then each member name, all null-terminated.
        public UdtTemplate DecodeUdtMeta(byte[] data)
        {
            var span = data.AsSpan();
            var templateId = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var instanceSize = BinaryPrimitives.ReadUInt32LittleEndian(span[6..]);
            var memberCount = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);

            const int headerSize = 14;
            const int memberInfoSize = 8;

            var fields = new (ushort Info, ushort Type, uint Offset)[memberCount];
            for (int i = 0; i < memberCount; i++)
            {
                var member = span[(headerSize + i * memberInfoSize)..];
                fields[i] = (
                    BinaryPrimitives.ReadUInt16LittleEndian(member),
                    BinaryPrimitives.ReadUInt16LittleEndian(member[2..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(member[4..]));
            }

            var names = ReadNullTerminatedStrings(span[(headerSize + memberCount * memberInfoSize)..], memberCount + 1);
            var templateName = names[0].Split(';')[0];

            var members = fields
                .Select((f, i) => new UdtTemplateMember(names[i + 1], f.Type, f.Offset, f.Info))
                .ToList();

            return new UdtTemplate(templateId, templateName, instanceSize, members);
        }

        private static string[] ReadNullTerminatedStrings(ReadOnlySpan<byte> data, int count)
        {
            var strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                var end = data.IndexOf((byte)0);
                if (end < 0) end = data.Length;
                strings[i] = Encoding.ASCII.GetString(data[..end]);
                data = end < data.Length ? data[(end + 1)..] : ReadOnlySpan<byte>.Empty;
            }
            return strings;
        }

        public static string DecodeControllerInfo(byte[] buffer)
        {
            var offset = 10;
            var major = buffer[offset].ToString();
            offset += 1;
            var minor = buffer[offset].ToString();
            offset += 8;
            var model = Encoding.ASCII.GetString(buffer, offset, buffer.Length - offset).TrimEnd('\0');

            return $"{model} v{major}.{minor}";
        }
    }
}
