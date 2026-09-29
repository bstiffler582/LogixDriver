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

        public INativeTag WriteTag(INativeTag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            return queue.EnqueueWriteSync(tag, encode, readModifyWrite).Tag;
        }

        public async Task<INativeTag> WriteTagAsync(INativeTag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            return (await queue.EnqueueWriteAsync(tag, encode, readModifyWrite).ConfigureAwait(false)).Tag;
        }

        public INativeTag Initialize(INativeTag tag)
        {
            return queue.EnqueueInitializeSync(tag).Tag;
        }

        public async Task<INativeTag> InitializeAsync(INativeTag tag)
        {
            return (await queue.EnqueueInitializeAsync(tag).ConfigureAwait(false)).Tag;
        }
    }
}
