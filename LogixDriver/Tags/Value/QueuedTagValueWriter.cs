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

        public Tag WriteTag(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            return queue.EnqueueWriteSync(tag, encode, readModifyWrite).Tag;
        }

        public async Task<Tag> WriteTagAsync(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            return (await queue.EnqueueWriteAsync(tag, encode, readModifyWrite).ConfigureAwait(false)).Tag;
        }

        public Tag Initialize(Tag tag)
        {
            return queue.EnqueueInitializeSync(tag).Tag;
        }

        public async Task<Tag> InitializeAsync(Tag tag)
        {
            return (await queue.EnqueueInitializeAsync(tag).ConfigureAwait(false)).Tag;
        }
    }
}
