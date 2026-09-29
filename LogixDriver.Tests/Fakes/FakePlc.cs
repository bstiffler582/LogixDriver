using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Logix.Tags;

namespace LogixDriver.Tests.Fakes
{
    /// <summary>
    /// Serves metadata responses (@tags, Program:X.@tags, @udt/{id}) encoded the way a Logix
    /// controller returns them, and counts every raw read by name.
    /// </summary>
    internal sealed class FakePlc : ITagValueReader
    {
        private readonly Dictionary<string, byte[]> responses = new();
        public ConcurrentDictionary<string, int> RawReads { get; } = new();

        public int ReadsOf(string name) => RawReads.TryGetValue(name, out var n) ? n : 0;
        public int TemplateReads => RawReads.Where(r => r.Key.StartsWith("@udt/")).Sum(r => r.Value);

        public FakePlc Tags(params TagEntry[] tags) => Respond("@tags", EncodeTagList(tags));
        public FakePlc ProgramTags(string program, params TagEntry[] tags) => Respond($"{program}.@tags", EncodeTagList(tags));
        public FakePlc Template(ushort id, string name, uint size, params MemberEntry[] members) =>
            Respond($"@udt/{id}", EncodeTemplate(id, name, size, members));

        public FakePlc Respond(string name, byte[] data)
        {
            responses[name] = data;
            return this;
        }

        public Task<byte[]> ReadRawAsync(string tagName, int elementCount = 1)
        {
            RawReads.AddOrUpdate(tagName, 1, (_, n) => n + 1);
            return responses.TryGetValue(tagName, out var data)
                ? Task.FromResult((byte[])data.Clone())
                : Task.FromException<byte[]>(new InvalidOperationException($"FakePlc has no response for '{tagName}'"));
        }

        public Task<byte[]> ReadBufferAsync(INativeTag tag) => throw new NotSupportedException();
        public byte[] ReadBuffer(INativeTag tag) => throw new NotSupportedException();

        public static byte[] EncodeTagList(IEnumerable<TagEntry> tags)
        {
            var bytes = new List<byte>();
            uint instance = 1;
            foreach (var t in tags)
            {
                var entry = new byte[22];
                BinaryPrimitives.WriteUInt32LittleEndian(entry, instance++);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), t.Type);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), t.ElementLength);
                for (int d = 0; d < 3; d++)
                    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8 + d * 4), d < t.Dims.Length ? t.Dims[d] : 0);
                var name = Encoding.ASCII.GetBytes(t.Name);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(20), (ushort)name.Length);
                bytes.AddRange(entry);
                bytes.AddRange(name);
            }
            return bytes.ToArray();
        }

        public static byte[] EncodeTemplate(ushort id, string name, uint size, IReadOnlyList<MemberEntry> members)
        {
            var header = new byte[14];
            BinaryPrimitives.WriteUInt16LittleEndian(header, id);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), size);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)members.Count);

            var bytes = new List<byte>(header);
            foreach (var m in members)
            {
                var info = new byte[8];
                BinaryPrimitives.WriteUInt16LittleEndian(info, m.Info);
                BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(2), m.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), m.Offset);
                bytes.AddRange(info);
            }

            // Logix appends extra data after ';' in the template name
            foreach (var s in members.Select(m => m.Name).Prepend($"{name};n\u0003"))
            {
                bytes.AddRange(Encoding.ASCII.GetBytes(s));
                bytes.Add(0);
            }
            return bytes.ToArray();
        }
    }

    internal record TagEntry(string Name, ushort Type, uint[] Dims, ushort ElementLength = 0)
    {
        public TagEntry(string name, ushort type) : this(name, type, Array.Empty<uint>()) { }
    }

    /// <param name="Info">bit number for BOOL members, element count for array members, else 0</param>
    internal record MemberEntry(string Name, ushort Type, uint Offset, ushort Info = 0);
}
