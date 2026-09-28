using libplctag;

namespace Logix.Tags
{
    internal class QueuedTagValueWriter : ITagValueWriter
    {
        private readonly ITagReadWriteQueue queue;

        public QueuedTagValueWriter(ITagReadWriteQueue queue)
        {
            this.queue = queue;
        }

        public Tag WriteTag(Tag tag, Action<byte[]>? encode = null)
        {
            return queue.EnqueueWriteSync(tag, encode).Tag;
        }

        public async Task<Tag> WriteTagAsync(Tag tag, Action<byte[]>? encode = null)
        {
            return (await queue.EnqueueWriteAsync(tag, encode)).Tag;
        }

        public Tag Initialize(Tag tag)
        {
            return queue.EnqueueInitializeSync(tag).Tag;
        }

        public async Task<Tag> InitializeAsync(Tag tag)
        {
            return (await queue.EnqueueInitializeAsync(tag)).Tag;
        }
    }
}
