using Logix.Tags;
using static Logix.Tags.TagMetaHelpers;

namespace LogixDriver.Tests
{
    public class TypeTests
    {
        [Fact]
        public void BoolArrayWordsBecomeBits()
        {
            var type = TypeRef.FromRaw(0x20D3, new uint[] { 2, 0, 0 });

            Assert.True(type.IsBitArray);
            Assert.Equal((ushort)Code.BOOL, type.Code);
            Assert.Equal(new[] { 64 }, type.Dims);
            Assert.Same(PrimitiveType.Bool, type.ElementType);
        }

        [Fact]
        public void ArrayFlagsAreStripped()
        {
            var type = TypeRef.FromRaw(0x4000 | 0x8123, new uint[] { 2, 3, 0 });

            Assert.Equal(0x8123, type.Code);
            Assert.Equal(new[] { 2, 3 }, type.Dims);
            Assert.True(type.IsStruct);
            Assert.False(type.IsResolved);
            Assert.Equal(new[] { 3 }, type.Index(1).Dims);
        }

        [Fact]
        public void PrimitivesAreResolvedImmediately()
        {
            var type = TypeRef.FromRaw((ushort)Code.REAL, Array.Empty<uint>());

            Assert.False(type.IsArray);
            Assert.Equal("REAL", type.ElementType!.Name);
            Assert.Equal(4, type.ElementType.Size);
        }

        [Fact]
        public void TemplateBuildHidesBoolHostsAndKeepsBitOffsets()
        {
            var type = (StructType)TypeResolver.Build(new UdtTemplate(0x123, "Recipe", 8, new[]
            {
                new UdtTemplateMember("ZZZZZZZZZZRecipe0", (ushort)Code.SINT, 0, 0),
                new UdtTemplateMember("Enable", (ushort)Code.BOOL, 0, 0),
                new UdtTemplateMember("Done", (ushort)Code.BOOL, 0, 3),
                new UdtTemplateMember("Count", (ushort)Code.SINT, 1, 0),
                new UdtTemplateMember("Flags", 0x20D3, 4, 1),
            }));

            Assert.Equal(new[] { "Enable", "Done", "Count", "Flags" }, type.Members.Select(m => m.Name));
            Assert.Equal(3, type.GetMember("Done")!.BitOffset);
            Assert.Equal(new[] { 32 }, type.GetMember("flags")!.Type.Dims);
        }

        [Fact]
        public void StringLayoutBecomesStringType()
        {
            var type = TypeResolver.Build(new UdtTemplate(0x0AB, "STRING20", 24, new[]
            {
                new UdtTemplateMember("LEN", (ushort)Code.DINT, 0, 0),
                new UdtTemplateMember("DATA", 0x20C2, 4, 20),
            }));

            var stringType = Assert.IsType<StringType>(type);
            Assert.Equal(20, stringType.Capacity);
            Assert.Equal("STRING20", stringType.Name);
        }
    }
}
