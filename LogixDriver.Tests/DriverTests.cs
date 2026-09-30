using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Logix.Driver;
using Logix.Tags;
using LogixDriver.Tests.Fakes;

namespace LogixDriver.Tests
{
    /// <summary>End to end: the real driver, queue and directory over fake native tags.</summary>
    public class DriverTests : IAsyncLifetime
    {
        private readonly FakeTagFactory factory = new(SampleController.Create());
        private Driver driver = null!;

        public async Task InitializeAsync()
        {
            driver = Driver.Create(new Target("test", "127.0.0.1"), factory);
            Assert.True(await driver.TryConnectAsync());
        }

        public Task DisposeAsync()
        {
            driver.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public void ConnectReadsControllerInfo() => Assert.Equal("1756-L83E v33.11", driver.ControllerInfo);

        [Fact]
        public async Task ReadsAndWritesMembers()
        {
            var temp = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(temp, 21.5f);
            factory.Data[("Recipes[3].Temp", 1)] = temp;
            factory.Data[("Program:Main.Local", 1)] = new byte[4];

            Assert.Equal(21.5f, await driver.ReadTagValueAsync("recipes[3].temp"));

            await driver.WriteTagValueAsync("Program:Main.Local", 1234);
            Assert.Equal(1234, BinaryPrimitives.ReadInt32LittleEndian(factory.Tag("Program:Main.Local", 1).PlcData));
        }

        [Fact]
        public async Task BitWritesShareTheirWordAndKeepNeighbouringBits()
        {
            factory.Data[("Bits[1]", 1)] = new byte[] { 0b0000_0001, 0, 0, 0 }; // bit 32 set

            await Task.WhenAll(
                driver.WriteTagValueAsync("Bits[37]", true),
                driver.WriteTagValueAsync("Bits[40]", true));

            Assert.Equal(new byte[] { 0b0010_0001, 0b0000_0001, 0, 0 }, factory.Tag("Bits[1]", 1).PlcData);
            Assert.Equal(1, factory.Created.Count(c => c == ("Bits[1]", 1)));
            Assert.Equal(true, await driver.ReadTagValueAsync("Bits[32]"));
        }

        [Fact]
        public async Task WholeArrayAndFirstElementUseSeparateHandles()
        {
            factory.Data[("Bits[0]", 2)] = new byte[] { 0b0000_1000, 0, 0, 0, 0, 0, 0, 0 };
            factory.Data[("Bits[0]", 1)] = new byte[] { 0b0000_1000, 0, 0, 0 };

            var all = (List<object>)(await driver.ReadTagValueAsync("Bits"))!;
            var one = await driver.ReadTagValueAsync("Bits[3]");

            Assert.Equal(64, all.Count);
            Assert.Equal(true, all[3]);
            Assert.Equal(true, one);
        }

        [Fact]
        public async Task LoadingAFilterDoesNotReadOtherTemplates()
        {
            await driver.LoadTagsAsync(new[] { "Counter" });

            Assert.Contains(driver.Tags.GetLoadedTags(), t => t.Name == "Recipes");
            Assert.DoesNotContain(factory.Created, c => c.Name.StartsWith("@udt/"));
        }

        /// <summary>Creates fake native tags: metadata from a FakePlc, @raw probe replies, and test-provided data.</summary>
        private sealed class FakeTagFactory : ITagFactory
        {
            private readonly FakePlc plc;
            private readonly ConcurrentDictionary<(string, int), FakeNativeTag> tags = new();

            public FakeTagFactory(FakePlc plc) => this.plc = plc;

            public ConcurrentDictionary<(string Name, int Count), byte[]> Data { get; } = new();
            public ConcurrentQueue<(string Name, int Count)> Created { get; } = new();

            public FakeNativeTag Tag(string name, int count) => tags[(name, count)];

            public INativeTag Create(string name, int elementCount = 1)
            {
                Created.Enqueue((name, elementCount));
                var tag = new FakeNativeTag(name, plc.TryGetResponse(name, out var meta) ? meta : Data.GetValueOrDefault((name, elementCount)));
                if (name == "@raw")
                    tag.OnWrite = _ => ControllerInfoReply("1756-L83E", 33, 11);
                if (!name.StartsWith('@') && !name.Contains(".@"))
                    tags[(name, elementCount)] = tag;
                return tag;
            }

            private static byte[] ControllerInfoReply(string model, byte major, byte minor)
            {
                var reply = new byte[19 + model.Length];
                reply[10] = major;
                reply[11] = minor;
                Encoding.ASCII.GetBytes(model).CopyTo(reply, 19);
                return reply;
            }
        }
    }
}
