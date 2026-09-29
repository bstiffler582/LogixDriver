using Logix.Tags;
using LogixDriver.Tests.Fakes;
using static Logix.Tags.TagMetaHelpers;

namespace LogixDriver.Tests
{
    public class TagMetaDecoderTests
    {
        private readonly TagMetaDecoder decoder = new();

        [Fact]
        public void DecodesTagList()
        {
            var data = FakePlc.EncodeTagList(new[]
            {
                new TagEntry("Counter", (ushort)Code.DINT),
                new TagEntry("Bits", 0x20D3, new uint[] { 2 }),
                new TagEntry("Program:Main", 0x1068),
            });

            var tags = decoder.DecodeTagList(data);

            Assert.Equal(new[] { "Counter", "Bits", "Program:Main" }, tags.Select(t => t.Name));
            Assert.Equal((ushort)Code.DINT, tags[0].Type);
            Assert.Equal(0x20D3, tags[1].Type);
            Assert.Equal(new uint[] { 2, 0, 0 }, tags[1].Dims);
        }

        [Fact]
        public void DecodesTemplate()
        {
            var data = FakePlc.EncodeTemplate(0x123, "Recipe", 24, new[]
            {
                new MemberEntry("ZZZZZZZZZZRecipe0", (ushort)Code.SINT, 0),
                new MemberEntry("Enable", (ushort)Code.BOOL, 0, Info: 0),
                new MemberEntry("Done", (ushort)Code.BOOL, 0, Info: 1),
                new MemberEntry("Temp", (ushort)Code.REAL, 4),
                new MemberEntry("Steps", 0x20C4, 8, Info: 4),
            });

            var template = decoder.DecodeUdtMeta(data);

            Assert.Equal(0x123, template.Id);
            Assert.Equal("Recipe", template.Name);
            Assert.Equal(24u, template.Size);
            Assert.Equal(new[] { "ZZZZZZZZZZRecipe0", "Enable", "Done", "Temp", "Steps" }, template.Members.Select(m => m.Name));
            Assert.Equal(1, template.Members[2].Info);
            Assert.Equal(4, template.Members[4].Info);
            Assert.Equal(8u, template.Members[4].Offset);
        }
    }
}
