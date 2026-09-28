namespace Logix.Tags
{
    /// <summary>
    /// Converts between raw tag data and values. Resolve buffers may be shared between
    /// concurrent readers of the same tag, so ResolveValue must not mutate them.
    /// </summary>
    public interface ITagValueResolver
    {
        Type ValueType { get; }
        object ResolveValue(byte[] buffer, TagDefinition definition, int offset = 0);
        void WriteTagBuffer(byte[] buffer, TagDefinition definition, object value, int offset = 0);
    }
    public interface ITagValueResolver<T> : ITagValueResolver
    {
        new T ResolveValue(byte[] buffer, TagDefinition definition, int offset = 0);
        void WriteTagBuffer(byte[] buffer, TagDefinition definition, T value, int offset = 0);
    }
}
