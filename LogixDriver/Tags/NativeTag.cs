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

    /// <summary>A native tag operation failed. <see cref="Status"/> is the tag's status after the failure.</summary>
    public sealed class NativeTagException : Exception
    {
        public string TagName { get; }
        public Status Status { get; }

        public NativeTagException(string tagName, Status status, Exception inner)
            : base($"{tagName}: {inner.Message} (status {status})", inner)
        {
            TagName = tagName;
            Status = status;
        }

        /// <summary>The failure looks like lost communication rather than a problem with this tag.</summary>
        public bool IsConnectionError =>
            Status is Status.ErrorBadConnection or Status.ErrorTimeout or Status.ErrorWinsock or Status.Pending
            // observed: an ErrorTimeout exception while the tag status is unrelated (e.g. NotFound)
            || InnerException?.Message == "ErrorTimeout";
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
