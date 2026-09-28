using libplctag;

namespace Logix.Tags
{
    public interface ITagValueWriter
    {
        /// <summary>
        /// Writes the tag. <paramref name="encode"/> (optional) receives a copy of the tag's current
        /// buffer to fill with the value; it's applied immediately before the write.
        /// </summary>
        public Task<Tag> WriteTagAsync(Tag tag, Action<byte[]>? encode = null);
        public Tag WriteTag(Tag tag, Action<byte[]>? encode = null);
        public Task<Tag> InitializeAsync(Tag tag);
        public Tag Initialize(Tag tag);
    }

    internal class TagValueWriter : ITagValueWriter
    {
        public Tag Initialize(Tag tag)
        {
            if (!tag.IsInitialized)
                tag.Initialize();
            return tag;
        }

        public async Task<Tag> InitializeAsync(Tag tag)
        {
            if (!tag.IsInitialized)
                await tag.InitializeAsync();

            return tag;
        }

        public Tag WriteTag(Tag tag, Action<byte[]>? encode = null)
        {
            ApplyEncode(tag, encode);
            tag.Write();
            return tag;
        }

        public async Task<Tag> WriteTagAsync(Tag tag, Action<byte[]>? encode = null)
        {
            ApplyEncode(tag, encode);
            await tag.WriteAsync();
            return tag;
        }

        private static void ApplyEncode(Tag tag, Action<byte[]>? encode)
        {
            if (encode is null)
                return;

            var buffer = tag.GetBuffer();
            encode(buffer);
            tag.SetBuffer(buffer);
        }
    }
}
