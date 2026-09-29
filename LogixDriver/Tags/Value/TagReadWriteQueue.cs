using libplctag;
using System.Threading.Channels;

namespace Logix.Tags
{
    /// <summary>
    /// Result of a queued operation. <see cref="Buffer"/> is a copy of the tag's data taken while
    /// the operation still held the per-tag lock, so it can't be torn by later ops on the same tag.
    /// Reads: the data read. Writes: the data written. Initialize: empty.
    /// The buffer may be shared between coalesced readers — treat it as read-only.
    /// </summary>
    public readonly record struct TagSnapshot(Tag Tag, byte[] Buffer);

    public interface ITagReadWriteQueue : IDisposable
    {
        public Task<TagSnapshot> EnqueueReadAsync(Tag tag);
        public TagSnapshot EnqueueReadSync(Tag tag);
        public Task<TagSnapshot> EnqueueInitializeAsync(Tag tag);
        public TagSnapshot EnqueueInitializeSync(Tag tag);
        // encode (optional) receives a copy of the tag's current buffer and fills in the value to write.
        // It runs under the per-tag lock, immediately before the write.
        // readModifyWrite: read the tag first, in the same per-tag slot, so encode starts from fresh data.
        // For writes that only touch part of the buffer (e.g. one bit of a BOOL array word). These
        // writes are never coalesced, since each one carries a different partial change.
        public Task<TagSnapshot> EnqueueWriteAsync(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false);
        public TagSnapshot EnqueueWriteSync(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false);
        public void Flush();
        // Environment.TickCount64-style timestamp of the last successful op. 0 if none.
        public long LastActivityAt { get; }
    }

    /// <summary>
    /// Producer/consumer queues for managing async tag read/write operations.
    /// A single consumer loop dispatches ops by priority (init -> write -> read), running up to
    /// maxConcurrency in parallel with at most one in flight per tag.
    /// Ops are keyed by Tag instance, not name: two Tags can share a name but differ in element
    /// count (e.g. a whole array and its first element are both "Arr[0]").
    /// </summary>
    internal class TagReadWriteQueue : ITagReadWriteQueue
    {
        private abstract record QueuedOperation(Tag Tag)
        {
            public TaskCompletionSource<TagSnapshot> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record ReadOperation(Tag Tag) : QueuedOperation(Tag);
        private sealed record WriteOperation(Tag Tag, Action<byte[]>? Encode, bool ReadModifyWrite) : QueuedOperation(Tag);
        private sealed record InitializeOperation(Tag Tag) : QueuedOperation(Tag);

        private readonly ChannelWriter<ReadOperation> readChannelWriter;
        private readonly ChannelReader<ReadOperation> readChannelReader;
        private readonly ChannelWriter<InitializeOperation> initChannelWriter;
        private readonly ChannelReader<InitializeOperation> initChannelReader;
        private readonly ChannelWriter<WriteOperation> writeChannelWriter;
        private readonly ChannelReader<WriteOperation> writeChannelReader;
        
        private Task? consumerTask;
        private CancellationTokenSource? consumerCts;

        // Track pending operations by type and tag to prevent duplicates (Tag is sealed with reference equality)
        private readonly Dictionary<(Type Kind, Tag Tag), QueuedOperation> pendingOperations = new();

        // Caps total in-flight ops against libplctag.
        private readonly SemaphoreSlim globalConcurrency;
        private readonly int maxConcurrency;

        // Per-tag serialization: at most one op per tag is in flight (busyTags). Ops for a busy
        // tag are parked here — without holding a global slot — and re-queued when the tag's current
        // op completes. Cross-tag ops run in parallel up to the global concurrency cap.
        private readonly object tagStateLock = new();
        private readonly HashSet<Tag> busyTags = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Tag, List<QueuedOperation>> parkedOperations = new(ReferenceEqualityComparer.Instance);

        // Activity tracking: timestamp (Environment.TickCount64) of the last successful op.
        // Exposed for external liveness monitoring; the queue itself does no idle detection.
        private long lastActivityTicks;
        public long LastActivityAt => Volatile.Read(ref lastActivityTicks);

        public TagReadWriteQueue(int maxConcurrency = 8)
        {
            if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
            this.maxConcurrency = maxConcurrency;
            globalConcurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            // Separate unbounded channels for reads and writes
            var readChannel = Channel.CreateUnbounded<ReadOperation>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var initChannel = Channel.CreateUnbounded<InitializeOperation>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var writeChannel = Channel.CreateUnbounded<WriteOperation>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            readChannelWriter = readChannel.Writer;
            readChannelReader = readChannel.Reader;
            initChannelWriter = initChannel.Writer;
            initChannelReader = initChannel.Reader;
            writeChannelWriter = writeChannel.Writer;
            writeChannelReader = writeChannel.Reader;

            StartConsumer();
        }

        /// <summary>
        /// Enqueue a read operation and return the value asynchronously.
        /// If a read for the same tag is already pending, returns the existing task.
        /// </summary>
        public Task<TagSnapshot> EnqueueReadAsync(Tag tag)
        {
            lock (pendingOperations)
            {
                var operationKey = (typeof(ReadOperation), tag);

                // If operation already pending, return existing task
                if (pendingOperations.TryGetValue(operationKey, out var existing) && existing is ReadOperation readOp)
                    return readOp.CompletionSource.Task;

                var operation = new ReadOperation(tag);
                if (!readChannelWriter.TryWrite(operation))
                    throw new InvalidOperationException("Failed to enqueue read operation. Queue may be closed.");

                pendingOperations[operationKey] = operation;
                return operation.CompletionSource.Task;
            }
        }

        /// <summary>
        /// Enqueue a read operation and wait synchronously for the result
        /// </summary>
        public TagSnapshot EnqueueReadSync(Tag tag)
        {
            var task = EnqueueReadAsync(tag);
            return task.GetAwaiter().GetResult();
        }

        public Task<TagSnapshot> EnqueueInitializeAsync(Tag tag)
        {
            lock (pendingOperations)
            {
                var operationKey = (typeof(InitializeOperation), tag);

                var operation = new InitializeOperation(tag);

                // If a write already exists for this tag, cancel it and replace with new one
                if (pendingOperations.TryGetValue(operationKey, out var existing) && existing is InitializeOperation)
                {
                    existing.CompletionSource.TrySetCanceled();
                }

                if (!initChannelWriter.TryWrite(operation))
                    throw new InvalidOperationException("Failed to enqueue initialize operation. Queue may be closed.");

                pendingOperations[operationKey] = operation;
                return operation.CompletionSource.Task;
            }
        }

        public TagSnapshot EnqueueInitializeSync(Tag tag)
        {
            var task = EnqueueInitializeAsync(tag);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Enqueue a write operation and return completion asynchronously.
        /// Newer writes to the same tag replace older pending writes, except read-modify-writes,
        /// which each run in order.
        /// </summary>
        public Task<TagSnapshot> EnqueueWriteAsync(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            var operation = new WriteOperation(tag, encode, readModifyWrite);

            if (readModifyWrite)
            {
                // not tracked for coalescing; Flush still cancels it via the channel or parked list
                if (!writeChannelWriter.TryWrite(operation))
                    throw new InvalidOperationException("Failed to enqueue write operation. Queue may be closed.");
                return operation.CompletionSource.Task;
            }

            lock (pendingOperations)
            {
                var operationKey = (typeof(WriteOperation), tag);

                // If a write already exists for this tag, cancel it and replace with new one
                if (pendingOperations.TryGetValue(operationKey, out var existing) && existing is WriteOperation)
                {
                    existing.CompletionSource.TrySetCanceled();
                }

                if (!writeChannelWriter.TryWrite(operation))
                    throw new InvalidOperationException("Failed to enqueue write operation. Queue may be closed.");

                pendingOperations[operationKey] = operation;
                return operation.CompletionSource.Task;
            }
        }

        /// <summary>
        /// Enqueue a write operation and wait synchronously for completion
        /// </summary>
        public TagSnapshot EnqueueWriteSync(Tag tag, Action<byte[]>? encode = null, bool readModifyWrite = false)
        {
            var task = EnqueueWriteAsync(tag, encode, readModifyWrite);
            return task.GetAwaiter().GetResult();
        }

        private void MarkActivity() => Volatile.Write(ref lastActivityTicks, Environment.TickCount64);

        /// <summary>
        /// Cancel all queued and tracked operations without tearing down the queue.
        /// In-flight (already dispatched) ops continue to completion in the background;
        /// their TCSes are already cancelled here so callers see the cancellation immediately.
        /// </summary>
        public void Flush()
        {
            while (initChannelReader.TryRead(out var op)) op.CompletionSource.TrySetCanceled();
            while (writeChannelReader.TryRead(out var op)) op.CompletionSource.TrySetCanceled();
            while (readChannelReader.TryRead(out var op)) op.CompletionSource.TrySetCanceled();

            lock (pendingOperations)
            {
                foreach (var op in pendingOperations.Values)
                    op.CompletionSource.TrySetCanceled();
                pendingOperations.Clear();
            }

            // busyTags is left alone: those tags still have native ops in flight
            CancelParkedOperations();
        }

        private void CancelParkedOperations()
        {
            lock (tagStateLock)
            {
                foreach (var parked in parkedOperations.Values)
                    foreach (var op in parked)
                        op.CompletionSource.TrySetCanceled();
                parkedOperations.Clear();
            }
        }

        private void StartConsumer()
        {
            consumerCts = new CancellationTokenSource();
            consumerTask = Task.Run(() => ConsumeLoop(consumerCts.Token), consumerCts.Token);
        }

        private async Task ConsumeLoop(CancellationToken cancel)
        {
            try
            {
                Task<bool>? initWait = null, writeWait = null, readWait = null;

                while (!cancel.IsCancellationRequested)
                {
                    // Take a slot before choosing the op, so each freed slot goes to the highest-priority
                    // op available at that moment. Ops are picked one at a time, so a steady stream of
                    // reads can't hold writes back until the read channel happens to drain.
                    await globalConcurrency.WaitAsync(cancel).ConfigureAwait(false);

                    QueuedOperation? operation;
                    try
                    {
                        while ((operation = TryTakeNext()) is null)
                        {
                            initWait ??= initChannelReader.WaitToReadAsync(cancel).AsTask();
                            writeWait ??= writeChannelReader.WaitToReadAsync(cancel).AsTask();
                            readWait ??= readChannelReader.WaitToReadAsync(cancel).AsTask();

                            await Task.WhenAny(initWait, writeWait, readWait).ConfigureAwait(false);
                            cancel.ThrowIfCancellationRequested();

                            if (initWait.IsCompleted) initWait = null;
                            if (writeWait.IsCompleted) writeWait = null;
                            if (readWait.IsCompleted) readWait = null;
                        }
                    }
                    catch
                    {
                        // don't leak the slot, or Dispose waits out its drain timeout for it
                        globalConcurrency.Release();
                        throw;
                    }

                    Dispatch(operation, cancel);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                // Expected shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical error in queue processing loop: {ex}");
            }
        }

        // Returns the highest-priority runnable op (init -> write -> read), or null if none.
        // Superseded/flushed ops are dropped and ops for busy tags are parked — neither consumes
        // the slot the caller is holding.
        private QueuedOperation? TryTakeNext()
        {
            while (TryReadByPriority(out var operation))
            {
                if (operation.CompletionSource.Task.IsCompleted)
                {
                    RemoveIfCurrent(operation);
                    continue;
                }

                lock (tagStateLock)
                {
                    if (busyTags.Add(operation.Tag))
                        return operation;

                    if (!parkedOperations.TryGetValue(operation.Tag, out var parked))
                        parkedOperations[operation.Tag] = parked = new List<QueuedOperation>();
                    parked.Add(operation);
                }
            }

            return null;
        }

        private bool TryReadByPriority(out QueuedOperation operation)
        {
            if (initChannelReader.TryRead(out var initOperation)) { operation = initOperation; return true; }
            if (writeChannelReader.TryRead(out var writeOperation)) { operation = writeOperation; return true; }
            if (readChannelReader.TryRead(out var readOperation)) { operation = readOperation; return true; }
            operation = null!;
            return false;
        }

        // Runs the op in the background; the caller has already taken its global slot and marked
        // its tag busy. Both are released on completion, parked ops for the tag first, so they're
        // back in their channels before the consumer loop can pick up the freed slot.
        private void Dispatch(QueuedOperation operation, CancellationToken cancel)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessOperation(operation, cancel).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseTag(operation.Tag);
                    globalConcurrency.Release();
                }
            });
        }

        private void ReleaseTag(Tag tag)
        {
            List<QueuedOperation>? parked;
            lock (tagStateLock)
            {
                busyTags.Remove(tag);
                parkedOperations.Remove(tag, out parked);
            }

            if (parked is null)
                return;

            foreach (var operation in parked)
            {
                var requeued = operation switch
                {
                    InitializeOperation initOperation => initChannelWriter.TryWrite(initOperation),
                    WriteOperation writeOperation => writeChannelWriter.TryWrite(writeOperation),
                    ReadOperation readOperation => readChannelWriter.TryWrite(readOperation),
                    _ => false
                };

                // channels are completed during Dispose
                if (!requeued)
                {
                    operation.CompletionSource.TrySetCanceled();
                    RemoveIfCurrent(operation);
                }
            }
        }

        // Remove the op from the pending dict only if it's still the one tracked under that key.
        // After a Flush() or a coalescing replace, a different op may now hold the key — leave it alone.
        private void RemoveIfCurrent(QueuedOperation operation)
        {
            var key = OperationKey(operation);
            lock (pendingOperations)
            {
                if (pendingOperations.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                    pendingOperations.Remove(key);
            }
        }

        private static (Type Kind, Tag Tag) OperationKey(QueuedOperation operation) => (operation.GetType(), operation.Tag);

        private async Task ProcessOperation(QueuedOperation operation, CancellationToken cancel)
        {
            // Superseded (coalesced) or flushed ops already have a completed TCS, so nobody is
            // waiting on the result — skip the round trip instead of spending it on the wire.
            // RemoveIfCurrent is a no-op when a replacement op now holds the key, which is the
            // usual case here.
            if (operation.CompletionSource.Task.IsCompleted)
            {
                RemoveIfCurrent(operation);
                return;
            }

            try
            {
                switch (operation)
                {
                    // Buffer access happens here, while this op is the only one in flight for its tag,
                    // so reads can't be torn and a pending write's data can't be overwritten by a read.
                    case ReadOperation readOp:
                        await readOp.Tag.ReadAsync(cancel).ConfigureAwait(false);
                        MarkActivity();
                        readOp.CompletionSource.TrySetResult(new TagSnapshot(readOp.Tag, readOp.Tag.GetBuffer()));
                        break;

                    case InitializeOperation initOp:
                        if (!initOp.Tag.IsInitialized)
                            await initOp.Tag.InitializeAsync(cancel).ConfigureAwait(false);
                        MarkActivity();
                        initOp.CompletionSource.TrySetResult(new TagSnapshot(initOp.Tag, Array.Empty<byte>()));
                        break;

                    case WriteOperation writeOp:
                        // same slot as the write, so no other op on this tag can land in between
                        if (writeOp.ReadModifyWrite)
                            await writeOp.Tag.ReadAsync(cancel).ConfigureAwait(false);

                        var written = Array.Empty<byte>();
                        if (writeOp.Encode is not null)
                        {
                            // start from the current buffer so bytes the encoder doesn't touch
                            // (host bits of packed BOOLs, padding) keep their last-read values
                            written = writeOp.Tag.GetBuffer();
                            writeOp.Encode(written);
                            writeOp.Tag.SetBuffer(written);
                        }
                        await writeOp.Tag.WriteAsync(cancel).ConfigureAwait(false);
                        MarkActivity();
                        writeOp.CompletionSource.TrySetResult(new TagSnapshot(writeOp.Tag, written));
                        break;
                }
            }
            catch (Exception ex)
            {
                operation.CompletionSource.TrySetException(ex);
            }
            finally
            {
                RemoveIfCurrent(operation);
            }
        }

        public void Dispose()
        {
            initChannelWriter.TryComplete();
            writeChannelWriter.TryComplete();
            readChannelWriter.TryComplete();

            consumerCts?.Cancel();

            try
            {
                consumerTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Expected — OCE wrapped
            }

            // Drain in-flight dispatched ops by acquiring all global permits.
            // Bounded wait so a stuck libplctag call can't hold dispose forever.
            for (int i = 0; i < maxConcurrency; i++)
            {
                try { globalConcurrency.Wait(TimeSpan.FromSeconds(5)); }
                catch (ObjectDisposedException) { break; }
            }

            lock (pendingOperations)
            {
                foreach (var operation in pendingOperations)
                    operation.Value.CompletionSource.TrySetCanceled();
                pendingOperations.Clear();
            }

            CancelParkedOperations();

            globalConcurrency.Dispose();
            consumerCts?.Dispose();
        }
    }
}