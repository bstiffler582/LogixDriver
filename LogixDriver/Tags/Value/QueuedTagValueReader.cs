using Logix.Driver;
using libplctag;

namespace Logix.Tags
{
    internal class QueuedTagValueReader : ITagValueReader
    {
        private readonly ITagFactory tagFactory;
        private readonly ITagReadWriteQueue queue;

        public QueuedTagValueReader(ITagFactory tagFactory, ITagReadWriteQueue queue)
        {
            this.tagFactory = tagFactory;
            this.queue = queue;
        }

        public async Task<byte[]> ReadBufferAsync(INativeTag tag)
        {
            return (await queue.EnqueueReadAsync(tag).ConfigureAwait(false)).Buffer;
        }

        public byte[] ReadBuffer(INativeTag tag)
        {
            return queue.EnqueueReadSync(tag).Buffer;
        }

        public async Task<byte[]> ReadRawAsync(string tagName, int elementCount = 1)
        {
            using var tag = tagFactory.Create(tagName, elementCount);
            return (await queue.EnqueueReadAsync(tag).ConfigureAwait(false)).Buffer;
        }
    }
}
