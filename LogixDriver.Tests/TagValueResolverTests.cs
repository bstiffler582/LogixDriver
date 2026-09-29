using System.Buffers.Binary;
using System.Text;
using Logix.Tags;
using LogixDriver.Tests.Fakes;
using static LogixDriver.Tests.Fakes.SampleController;

namespace LogixDriver.Tests
{
    public class TagValueResolverTests
    {
        private readonly TagDirectory directory = new(SampleController.Create());
        private readonly DefaultTagValueResolver resolver = new();

        private static byte[] RecipeBytes(bool enable, bool done, float temp, int target, int time, uint flags)
        {
            var b = new byte[RecipeSize];
            b[0] = (byte)((enable ? 1 : 0) | (done ? 2 : 0));
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), temp);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), target);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), time);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), flags);
            return b;
        }

        [Fact]
        public async Task ReadsStructures()
        {
            var tag = await directory.ResolveAsync("Recipes[3]");
            var value = (Dictionary<string, object>)resolver.ResolveValue(RecipeBytes(false, true, 1.5f, 7, 9, 0x8000_0001), tag.Type);

            Assert.Equal(new[] { "Enable", "Done", "Temp", "Step", "Flags" }, value.Keys);
            Assert.Equal(false, value["Enable"]);
            Assert.Equal(true, value["Done"]);
            Assert.Equal(1.5f, value["Temp"]);
            Assert.Equal(7, ((Dictionary<string, object>)value["Step"])["Target"]);
            var flags = (List<object>)value["Flags"];
            Assert.Equal(32, flags.Count);
            Assert.Equal(new[] { 0, 31 }, flags.Select((f, i) => ((bool)f, i)).Where(f => f.Item1).Select(f => f.i));
        }

        [Fact]
        public async Task StructuresRoundTrip()
        {
            var tag = await directory.ResolveAsync("Recipes[3]");
            var source = RecipeBytes(true, false, -3.25f, 100, 200, 0x0F0F_00F0);
            source[0] |= 0b1000_0000; // a host bit no member uses: left as it was

            var target = new byte[RecipeSize];
            target[0] = 0b1000_0000;
            resolver.WriteTagBuffer(target, tag.Type, resolver.ResolveValue(source, tag.Type));

            Assert.Equal(source, target);
        }

        [Fact]
        public async Task BoolArraysAreBitsAcrossWords()
        {
            var tag = await directory.ResolveAsync("Bits");
            var buffer = new byte[8];
            buffer[0] = 0b0010_0001;          // bits 0, 5
            buffer[4] = 0b0000_0010;          // bit 33

            var bits = (List<object>)resolver.ResolveValue(buffer, tag.Type);

            Assert.Equal(64, bits.Count);
            Assert.Equal(new[] { 0, 5, 33 }, bits.Select((b, i) => ((bool)b, i)).Where(b => b.Item1).Select(b => b.i));

            var written = new byte[8];
            resolver.WriteTagBuffer(written, tag.Type, bits.Cast<bool>().ToArray());
            Assert.Equal(buffer, written);
        }

        [Theory]
        [InlineData("Bits[37]", new byte[] { 0b0010_0000, 0, 0, 0 })]
        [InlineData("Bits[40]", new byte[] { 0, 0b0000_0001, 0, 0 })]
        [InlineData("Bits[63]", new byte[] { 0, 0, 0, 0b1000_0000 })]
        public async Task BoolArrayElementAddressesItsBitInTheWord(string path, byte[] expectedWord)
        {
            var tag = await directory.ResolveAsync(path);
            var word = new byte[4];

            resolver.WriteTagBuffer(word, tag.Type, true, 0, tag.BitOffset);

            Assert.Equal(expectedWord, word);
            Assert.Equal(true, resolver.ResolveValue(word, tag.Type, 0, tag.BitOffset));
        }

        [Fact]
        public async Task MultiDimensionalArraysNestPerDimension()
        {
            var tag = await directory.ResolveAsync("Grid");
            var buffer = new byte[6 * RecipeSize];
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(5 * RecipeSize + 4), 42f); // Grid[1,2].Temp

            var rows = (List<object>)resolver.ResolveValue(buffer, tag.Type);

            Assert.Equal(2, rows.Count);
            var row = (List<object>)rows[1];
            Assert.Equal(3, row.Count);
            Assert.Equal(42f, ((Dictionary<string, object>)row[2])["Temp"]);
        }

        [Fact]
        public async Task StringsUseTheirCapacity()
        {
            var tag = await directory.ResolveAsync("Label");
            var buffer = new byte[StringSize];

            resolver.WriteTagBuffer(buffer, tag.Type, "hello");

            Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(buffer));
            Assert.Equal("hello", Encoding.ASCII.GetString(buffer, 4, 5));
            Assert.Equal("hello", resolver.ResolveValue(buffer, tag.Type));
            Assert.Throws<ArgumentException>(() => resolver.WriteTagBuffer(buffer, tag.Type, new string('x', 83)));
        }

        [Fact]
        public async Task InvalidWriteValuesThrow()
        {
            var recipe = await directory.ResolveAsync("Recipes[0]");
            var bits = await directory.ResolveAsync("Bits");
            var buffer = new byte[RecipeSize];

            Assert.Throws<ArgumentException>(() => resolver.WriteTagBuffer(buffer, recipe.Type, new Dictionary<string, object> { ["Temp"] = 1f }));
            Assert.Throws<ArgumentException>(() => resolver.WriteTagBuffer(buffer, recipe.Type, 5));
            Assert.Throws<ArgumentException>(() => resolver.WriteTagBuffer(new byte[8], bits.Type, new bool[3]));
            Assert.Throws<ArgumentException>(() => resolver.WriteTagBuffer(new byte[8], bits.Type, "abc"));
        }
    }
}
