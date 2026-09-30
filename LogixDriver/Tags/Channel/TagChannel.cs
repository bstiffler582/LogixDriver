using System.Collections.Concurrent;

namespace Logix.Tags
{
    /// <summary>One-off reads by name, e.g. @tags and @udt/{id} metadata.</summary>
    public interface IRawTagReader
    {
        /// <summary>Creates a tag, reads it, disposes it and returns its data.</summary>
        Task<byte[]> ReadRawAsync(string tagName, int elementCount = 1);
    }

    /// <summary>
    /// All traffic to the controller. Tags are addressed by native name and element count; each
    /// pair gets one native tag, reused across calls. Failures of the native operation surface as
    /// <see cref="NativeTagException"/>; operations cancelled by <see cref="Flush"/> as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public interface ITagChannel : IRawTagReader, IDisposable
    {
        /// <summary>Reads a tag. The buffer may be shared with concurrent readers: treat it as read-only.</summary>
        Task<byte[]> ReadAsync(string nativeName, int elementCount);

        /// <summary>
        /// Writes a tag. <paramref name="encode"/> fills the value into a copy of the tag's buffer,
        /// immediately before the write. <paramref name="readModifyWrite"/> reads the tag first, for
        /// values that only change part of the buffer.
        /// </summary>
        Task WriteAsync(string nativeName, int elementCount, Action<byte[]> encode, bool readModifyWrite = false);

        /// <summary>Sends <paramref name="request"/> to a one-off tag (e.g. @raw) and returns the reply.</summary>
        Task<byte[]> RequestAsync(string tagName, byte[] request);

        /// <summary>Cancels every operation still waiting to run, e.g. when the connection is lost.</summary>
        void Flush();

        /// <summary>Environment.TickCount64 of the last successful operation, 0 if none.</summary>
        long LastActivityAt { get; }
    }

    internal sealed class TagChannel : ITagChannel
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        private readonly ITagFactory tagFactory;
        private readonly ConcurrentDictionary<(string Name, int ElementCount), Lazy<TagSession>> sessions = new();
        private CancellationTokenSource flushSource = new();
        private long lastActivity;
        private volatile bool disposed;

        public TagChannel(ITagFactory tagFactory, int maxConcurrency = 8)
        {
            this.tagFactory = tagFactory;
            Limiter = new PriorityLimiter(maxConcurrency);
        }

        internal PriorityLimiter Limiter { get; }
        internal CancellationToken FlushToken => Volatile.Read(ref flushSource).Token;
        public long LastActivityAt => Volatile.Read(ref lastActivity);

        public Task<byte[]> ReadAsync(string nativeName, int elementCount) =>
            Session(nativeName, elementCount).ReadAsync();

        public Task WriteAsync(string nativeName, int elementCount, Action<byte[]> encode, bool readModifyWrite = false) =>
            Session(nativeName, elementCount).WriteAsync(encode, readModifyWrite);

        public Task<byte[]> ReadRawAsync(string tagName, int elementCount = 1) =>
            RunOnceAsync(tagName, elementCount, highPriority: false, tag => tag.ReadAsync());

        public Task<byte[]> RequestAsync(string tagName, byte[] request) =>
            RunOnceAsync(tagName, 1, highPriority: true, tag =>
            {
                tag.SetBuffer(request);
                return tag.WriteAsync();
            });

        // The replaced source isn't disposed: operations may still hold its token.
        public void Flush() => Interlocked.Exchange(ref flushSource, new CancellationTokenSource()).Cancel();

        // One Lazy per key so concurrent first callers create a single native tag
        private TagSession Session(string nativeName, int elementCount)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sessions.GetOrAdd((nativeName, elementCount),
                key => new Lazy<TagSession>(() => new TagSession(tagFactory.Create(key.Name, key.ElementCount), this))).Value;
        }

        private async Task<byte[]> RunOnceAsync(string tagName, int elementCount, bool highPriority, Func<INativeTag, Task> operation)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var tag = tagFactory.Create(tagName, elementCount);
            using var slot = await Limiter.AcquireAsync(highPriority, FlushToken).ConfigureAwait(false);

            if (!tag.IsInitialized)
                await RunNativeAsync(tag, tag.InitializeAsync).ConfigureAwait(false);
            await RunNativeAsync(tag, () => operation(tag)).ConfigureAwait(false);
            return tag.GetBuffer();
        }

        /// <summary>Runs a native operation, recording activity on success and wrapping failures.</summary>
        internal async Task RunNativeAsync(INativeTag tag, Func<CancellationToken, Task> operation) =>
            await RunNativeAsync(tag, () => operation(CancellationToken.None)).ConfigureAwait(false);

        private async Task RunNativeAsync(INativeTag tag, Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
                Volatile.Write(ref lastActivity, Environment.TickCount64);
            }
            catch (Exception ex)
            {
                throw new NativeTagException(tag.Name, tag.GetStatus(), ex);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Flush();

            // wait out operations already running by taking every slot (never given back), before
            // disposing the tags under them; bounded so a stuck native call can't hold dispose forever
            using var timeout = new CancellationTokenSource(DrainTimeout);
            try
            {
                for (int i = 0; i < Limiter.Capacity; i++)
                    Limiter.AcquireAsync(true, timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }

            foreach (var session in sessions.Values)
                if (session.IsValueCreated)
                    session.Value.Dispose();
            sessions.Clear();
        }
    }
}
