using System.Collections.Concurrent;
using libplctag;
using Logix.Tags;
using LogixDriver.Tests.Fakes;

namespace LogixDriver.Tests
{
    public class TagChannelTests : IDisposable
    {
        private readonly ConcurrentDictionary<string, FakeNativeTag> tags = new();
        private readonly ConcurrentQueue<string> started = new();
        private readonly ConcurrencyCounter shared = new();
        private TagChannel channel;

        public TagChannelTests() => channel = Open(8);

        public void Dispose() => channel.Dispose();

        private TagChannel Open(int capacity)
        {
            var factory = new Factory(name =>
            {
                var tag = tags.GetOrAdd(name, n => new FakeNativeTag(n, new byte[4]));
                tag.Shared = shared;
                tag.OnStart = kind => started.Enqueue($"{name}:{kind}");
                return tag;
            });
            return new TagChannel(factory, capacity);
        }

        private FakeNativeTag Tag(string name, TimeSpan? delay = null)
        {
            var tag = tags.GetOrAdd(name, n => new FakeNativeTag(n, new byte[4]));
            tag.Delay = delay ?? TimeSpan.Zero;
            return tag;
        }

        private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(60);

        private async Task WhenStarted(string entry)
        {
            for (int i = 0; i < 200 && !started.Contains(entry); i++)
                await Task.Delay(5);
            Assert.Contains(entry, started);
        }

        [Fact]
        public async Task ConcurrentReadsShareReads()
        {
            var tag = Tag("A", Slow);
            tag.PlcData = new byte[] { 1, 2, 3, 4 };

            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => channel.ReadAsync("A", 1)));

            Assert.All(results, r => Assert.Equal(new byte[] { 1, 2, 3, 4 }, r));
            Assert.InRange(tag.Reads, 1, 2); // one in flight, at most one queued behind it
        }

        [Fact]
        public async Task OneOperationPerTagAtATime()
        {
            var tag = Tag("A", TimeSpan.FromMilliseconds(5));

            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => i % 3 == 0
                ? channel.WriteAsync("A", 1, b => b[0] = (byte)i)
                : channel.ReadAsync("A", 1)));

            Assert.Equal(1, tag.MaxConcurrentOps);
        }

        [Fact]
        public async Task ConcurrencyIsCappedAcrossTags()
        {
            channel.Dispose();
            channel = Open(2);
            foreach (var name in "ABCDEF")
                Tag(name.ToString(), TimeSpan.FromMilliseconds(30));

            await Task.WhenAll("ABCDEF".Select(n => channel.ReadAsync(n.ToString(), 1)));

            Assert.Equal(2, shared.Max);
        }

        [Fact]
        public async Task QueuedWritesGoBeforeQueuedReads()
        {
            channel.Dispose();
            channel = Open(1);
            foreach (var name in new[] { "A", "B", "C", "D", "W" })
                Tag(name).PlcData = new byte[4];
            Tag("A", Slow);

            var blocker = channel.ReadAsync("A", 1);
            await WhenStarted("A:init");
            var reads = new[] { "B", "C", "D" }.Select(n => channel.ReadAsync(n, 1)).ToArray();
            await Task.Delay(20);
            var write = channel.WriteAsync("W", 1, b => b[0] = 1);

            await Task.WhenAll(reads.Append(blocker).Append(write));

            var afterBlocker = started.Where(s => !s.StartsWith("A:")).ToList();
            Assert.StartsWith("W:", afterBlocker[0]);
        }

        [Fact]
        public async Task ConcurrentWritesAreSentTogetherAndAllComplete()
        {
            var tag = Tag("A", Slow);

            var first = channel.WriteAsync("A", 1, b => b[0] = 1);
            await WhenStarted("A:write");
            var later = Enumerable.Range(2, 5).Select(n => channel.WriteAsync("A", 1, b => b[0] = (byte)n)).ToArray();

            await Task.WhenAll(later.Prepend(first));

            Assert.Equal(6, tag.PlcData[0]);   // last value wins
            Assert.Equal(2, tag.Writes);       // the in-flight write, then one for all five queued
        }

        [Fact]
        public async Task BitWritesBatchIntoOneReadModifyWrite()
        {
            var tag = Tag("Word", Slow);
            tag.PlcData = new byte[] { 0x80, 0, 0, 0 };  // bit 7 set on the controller side

            var first = channel.WriteAsync("Word", 1, b => b[1] |= 1, readModifyWrite: true);
            await WhenStarted("Word:write");
            var bits = Enumerable.Range(0, 7).Select(bit => channel.WriteAsync("Word", 1, b => b[0] |= (byte)(1 << bit), readModifyWrite: true));

            await Task.WhenAll(bits.Prepend(first));

            Assert.Equal(new byte[] { 0xFF, 1, 0, 0 }, tag.PlcData);
            Assert.Equal(2, tag.Writes);
        }

        [Fact]
        public async Task AFailingEncoderFailsOnlyItsOwnCaller()
        {
            var tag = Tag("A", Slow);

            var first = channel.WriteAsync("A", 1, b => b[0] = 1);
            await WhenStarted("A:write");
            var good = channel.WriteAsync("A", 1, b => b[1] = 2);
            var bad = channel.WriteAsync("A", 1, b => { b[2] = 99; throw new ArgumentException("bad value"); });
            var alsoGood = channel.WriteAsync("A", 1, b => b[3] = 4);

            await Task.WhenAll(first, good, alsoGood);
            await Assert.ThrowsAsync<ArgumentException>(() => bad);
            Assert.Equal(new byte[] { 1, 2, 0, 4 }, tag.PlcData);
        }

        [Fact]
        public async Task FlushCancelsWaitingOperationsButNotRunningOnes()
        {
            channel.Dispose();
            channel = Open(1);
            Tag("A", Slow);
            Tag("B");

            var running = channel.ReadAsync("A", 1);
            await WhenStarted("A:init");
            var waiting = channel.ReadAsync("B", 1);
            channel.Flush();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await running;
            Assert.Equal(0, Tag("B").Reads);

            // new operations run normally after a flush
            await channel.ReadAsync("B", 1);
            Assert.Equal(1, Tag("B").Reads);
        }

        [Fact]
        public async Task NativeFailuresAreWrappedWithStatus()
        {
            var tag = Tag("A");
            tag.FailWith = new InvalidOperationException("ErrorBadConnection");
            tag.Status = Status.ErrorBadConnection;

            var ex = await Assert.ThrowsAsync<NativeTagException>(() => channel.ReadAsync("A", 1));

            Assert.Equal("A", ex.TagName);
            Assert.True(ex.IsConnectionError);

            tag.Status = Status.ErrorNotFound;
            tag.FailWith = new InvalidOperationException("ErrorNotFound");
            ex = await Assert.ThrowsAsync<NativeTagException>(() => channel.ReadAsync("A", 1));
            Assert.False(ex.IsConnectionError);
        }

        [Fact]
        public async Task OneOffTagsAreDisposed()
        {
            Tag("@tags").PlcData = new byte[] { 9 };
            Tag("@raw").OnWrite = request => request.Reverse().ToArray();

            Assert.Equal(new byte[] { 9 }, await channel.ReadRawAsync("@tags"));
            Assert.Equal(new byte[] { 3, 2, 1 }, await channel.RequestAsync("@raw", new byte[] { 1, 2, 3 }));
            Assert.True(Tag("@tags").IsDisposed);
            Assert.True(Tag("@raw").IsDisposed);
        }

        [Fact]
        public async Task DisposeWaitsForRunningOperationsThenDisposesTags()
        {
            var tag = Tag("A", Slow);
            var running = channel.ReadAsync("A", 1);
            await WhenStarted("A:init");

            channel.Dispose();

            Assert.True(running.IsCompletedSuccessfully);
            Assert.True(tag.IsDisposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.ReadAsync("A", 1));
        }

        private sealed class Factory : ITagFactory
        {
            private readonly Func<string, INativeTag> create;
            public Factory(Func<string, INativeTag> create) => this.create = create;
            public INativeTag Create(string name, int elementCount = 1) => create(name);
        }
    }
}
