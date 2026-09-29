namespace Logix.Tags
{
    /// <summary>
    /// Converts between raw tag data and values, driven by the tag's type. Resolve buffers may be
    /// shared between concurrent readers of the same tag, so ResolveValue must not mutate them.
    /// </summary>
    /// <remarks>
    /// <paramref name="offset"/> is the byte offset of the value within the buffer and
    /// <paramref name="bitOffset"/> the bit within that byte, which is only non-zero for BOOLs.
    /// </remarks>
    public interface ITagValueResolver
    {
        Type ValueType { get; }
        object ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0);
        void WriteTagBuffer(byte[] buffer, TypeRef type, object value, int offset = 0, int bitOffset = 0);
    }

    public interface ITagValueResolver<T> : ITagValueResolver
    {
        new T ResolveValue(byte[] buffer, TypeRef type, int offset = 0, int bitOffset = 0);
        void WriteTagBuffer(byte[] buffer, TypeRef type, T value, int offset = 0, int bitOffset = 0);
    }
}
