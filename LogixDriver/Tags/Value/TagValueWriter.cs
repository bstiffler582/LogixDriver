namespace Logix.Tags
{
    public interface ITagValueWriter
    {
        /// <summary>
        /// Writes the tag. <paramref name="encode"/> (optional) receives a copy of the tag's current
        /// buffer to fill with the value; it's applied immediately before the write.
        /// <paramref name="readModifyWrite"/> reads the tag first so encode starts from fresh data, for
        /// writes that only change part of the buffer.
        /// </summary>
        public Task<INativeTag> WriteTagAsync(INativeTag tag, Action<byte[]>? encode = null, bool readModifyWrite = false);
        public INativeTag WriteTag(INativeTag tag, Action<byte[]>? encode = null, bool readModifyWrite = false);
        public Task<INativeTag> InitializeAsync(INativeTag tag);
        public INativeTag Initialize(INativeTag tag);
    }
}
