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

        public async Task<byte[]> ReadBufferAsync(Tag tag)
        {
            return (await queue.EnqueueReadAsync(tag)).Buffer;
        }

        public byte[] ReadBuffer(Tag tag)
        {
            return queue.EnqueueReadSync(tag).Buffer;
        }

        public async Task<Tag> ReadTagAsync(string tagName, int elementCount = 1)
        {
            var tag = tagFactory.Create(tagName, elementCount);
            return (await queue.EnqueueReadAsync(tag)).Tag;
        }

        public Tag ReadTag(string tagName, int elementCount = 1)
        {
            var tag = tagFactory.Create(tagName, elementCount);
            return queue.EnqueueReadSync(tag).Tag;
        }
    }
}
