using libplctag;

namespace Logix.Tags
{
    public interface ITagValueReader
    {
        /// <summary>
        /// Reads the tag and returns a copy of its data. The copy may be shared with concurrent
        /// readers of the same tag — treat it as read-only.
        /// </summary>
        public Task<byte[]> ReadBufferAsync(Tag tag);
        public byte[] ReadBuffer(Tag tag);
        public Task<Tag> ReadTagAsync(string tagName, int elementCount = 1);
        public Tag ReadTag(string tagName, int elementCount = 1);
    }

    internal class TagValueReader : ITagValueReader
    {
        private readonly ITagFactory tagFactory;

        public TagValueReader(ITagFactory tagFactory)
        {
            this.tagFactory = tagFactory;
        }

        public async Task<byte[]> ReadBufferAsync(Tag tag)
        {
            await tag.ReadAsync().ConfigureAwait(false);
            return tag.GetBuffer();
        }

        public byte[] ReadBuffer(Tag tag)
        {
            return ReadBufferAsync(tag).GetAwaiter().GetResult();
        }

        public async Task<Tag> ReadTagAsync(string tagName, int elementCount = 1)
        {
            var tag = tagFactory.Create(tagName, elementCount);
            await tag.ReadAsync().ConfigureAwait(false);
            return tag;
        }

        public Tag ReadTag(string tagName, int elementCount = 1)
        {
            return ReadTagAsync(tagName, elementCount).GetAwaiter().GetResult();
        }
    }
}
