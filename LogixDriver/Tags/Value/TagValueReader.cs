namespace Logix.Tags
{
    public interface ITagValueReader
    {
        /// <summary>
        /// Reads the tag and returns a copy of its data. The copy may be shared with concurrent
        /// readers of the same tag — treat it as read-only.
        /// </summary>
        public Task<byte[]> ReadBufferAsync(INativeTag tag);
        public byte[] ReadBuffer(INativeTag tag);
        /// <summary>
        /// One-off read by name (e.g. @tags, @udt/{id}): creates a tag, reads it, disposes it and
        /// returns its data.
        /// </summary>
        public Task<byte[]> ReadRawAsync(string tagName, int elementCount = 1);
    }
}
