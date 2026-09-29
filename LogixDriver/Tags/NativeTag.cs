using libplctag;

namespace Logix.Tags
{
    /// <summary>
    /// One native tag handle: the operations the driver needs from libplctag, behind an interface
    /// so the layers above can be tested without a controller. A handle supports only one
    /// operation at a time; callers are responsible for serializing access.
    /// </summary>
    public interface INativeTag : IDisposable
    {
        string Name { get; }
        bool IsInitialized { get; }
        Task InitializeAsync(CancellationToken ct = default);
        Task ReadAsync(CancellationToken ct = default);
        Task WriteAsync(CancellationToken ct = default);
        /// <summary>A copy of the tag's current data.</summary>
        byte[] GetBuffer();
        /// <summary>Replaces the tag's data, resizing it if the length differs.</summary>
        void SetBuffer(byte[] buffer);
        Status GetStatus();
    }

    internal sealed class LibPlcTag : INativeTag
    {
        private readonly Tag tag;

        public LibPlcTag(Tag tag) => this.tag = tag;

        public string Name => tag.Name;
        public bool IsInitialized => tag.IsInitialized;
        public Task InitializeAsync(CancellationToken ct = default) => tag.InitializeAsync(ct);
        public Task ReadAsync(CancellationToken ct = default) => tag.ReadAsync(ct);
        public Task WriteAsync(CancellationToken ct = default) => tag.WriteAsync(ct);
        public byte[] GetBuffer() => tag.GetBuffer();

        public void SetBuffer(byte[] buffer)
        {
            if (tag.GetSize() != buffer.Length)
                tag.SetSize(buffer.Length);
            tag.SetBuffer(buffer);
        }

        public Status GetStatus() => tag.GetStatus();
        public void Dispose() => tag.Dispose();
    }
}
