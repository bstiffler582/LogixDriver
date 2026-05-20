using libplctag;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Logix.Tags
{
    public interface ITagReadWriteQueue : IDisposable
    {
        public Task<Tag> EnqueueReadAsync(Tag tag);
        public Tag EnqueueReadSync(Tag tag);
        public Task<Tag> EnqueueInitializeAsync(Tag tag);
        public Tag EnqueueInitializeSync(Tag tag);
        public Task<Tag> EnqueueWriteAsync(Tag tag);
        public Tag EnqueueWriteSync(Tag tag);
        public void Flush();
        // Environment.TickCount64-style timestamp of the last successful op. 0 if none.
        public long LastActivityAt { get; }
    }

    /// <summary>
    /// Producer/consumer queues for managing async tag read/write operations.
    /// Ensures read/write operations are handled on a single task/thread.
    /// Executes cyclically with a max number of operations per cycle.
    /// </summary>
    internal class TagReadWriteQueue : ITagReadWriteQueue
    {
        private abstract record QueuedOperation(string TagName)
        {
            public TaskCompletionSource<Tag> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record ReadOperation(Tag Tag) : QueuedOperation(Tag.Name);
        private sealed record WriteOperation(Tag Tag) : QueuedOperation(Tag.Name);
        private sealed record InitializeOperation(Tag Tag) : QueuedOperation(Tag.Name);

        private readonly ChannelWriter<ReadOperation> readChannelWriter;
        private readonly ChannelReader<ReadOperation> readChannelReader;
        private readonly ChannelWriter<InitializeOperation> initChannelWriter;
        private readonly ChannelReader<InitializeOperation> initChannelReader;
        private readonly ChannelWriter<WriteOperation> writeChannelWriter;
        private readonly ChannelReader<WriteOperation> writeChannelReader;
        
        private Task? consumerTask;
        private CancellationTokenSource? consumerCts;

        // Track pending operations by tag name and type to prevent duplicates
        private readonly Dictionary<string, QueuedOperation> pendingOperations = new();

        // Caps total in-flight ops against libplctag.
        private readonly SemaphoreSlim globalConcurrency;
        private readonly int maxConcurrency;

        // Per-tag mutex: serializes ops touching the same native tag handle.
        // Cross-tag ops run in parallel up to the global concurrency cap.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> perTagLocks = new();

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
        public Task<Tag> EnqueueReadAsync(Tag tag)
        {
            lock (pendingOperations)
            {
                var operationKey = $"READ:{tag.Name}";

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
        public Tag EnqueueReadSync(Tag tag)
        {
            var task = EnqueueReadAsync(tag);
            return task.GetAwaiter().GetResult();
        }

        public Task<Tag> EnqueueInitializeAsync(Tag tag)
        {
            lock (pendingOperations)
            {
                var operationKey = $"INIT:{tag.Name}";

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

        public Tag EnqueueInitializeSync(Tag tag)
        {
            var task = EnqueueInitializeAsync(tag);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Enqueue a write operation and return completion asynchronously.
        /// Newer writes to the same tag replace older pending writes.
        /// </summary>
        public Task<Tag> EnqueueWriteAsync(Tag tag)
        {
            lock (pendingOperations)
            {
                var operationKey = $"WRITE:{tag.Name}";

                var operation = new WriteOperation(tag);

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
        public Tag EnqueueWriteSync(Tag tag)
        {
            var task = EnqueueWriteAsync(tag);
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
                    initWait ??= initChannelReader.WaitToReadAsync(cancel).AsTask();
                    writeWait ??= writeChannelReader.WaitToReadAsync(cancel).AsTask();
                    readWait ??= readChannelReader.WaitToReadAsync(cancel).AsTask();

                    await Task.WhenAny(initWait, writeWait, readWait);

                    // priority: init -> write -> read
                    while (initChannelReader.TryRead(out var initOperation))
                        await DispatchAsync(initOperation, cancel);

                    while (writeChannelReader.TryRead(out var writeOperation))
                        await DispatchAsync(writeOperation, cancel);

                    while (readChannelReader.TryRead(out var readOperation))
                        await DispatchAsync(readOperation, cancel);

                    if (initWait.IsCompleted) initWait = null;
                    if (writeWait.IsCompleted) writeWait = null;
                    if (readWait.IsCompleted) readWait = null;
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

        // Acquire a global slot, then start the actual operation work as a fire-and-forget task.
        // The consumer loop only blocks on the global semaphore — once a slot is acquired it
        // moves on to the next op, so cross-tag work proceeds in parallel.
        private async Task DispatchAsync(QueuedOperation operation, CancellationToken cancel)
        {
            try
            {
                await globalConcurrency.WaitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                operation.CompletionSource.TrySetCanceled(cancel);
                RemoveIfCurrent(operation);
                return;
            }

            _ = Task.Run(async () =>
            {
                var tagLock = perTagLocks.GetOrAdd(operation.TagName, _ => new SemaphoreSlim(1, 1));
                try
                {
                    await tagLock.WaitAsync(cancel);
                    try
                    {
                        await ProcessOperation(operation, cancel);
                    }
                    finally
                    {
                        tagLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    operation.CompletionSource.TrySetCanceled(cancel);
                    RemoveIfCurrent(operation);
                }
                finally
                {
                    globalConcurrency.Release();
                }
            });
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

        private static string OperationKey(QueuedOperation operation) => operation switch
        {
            ReadOperation => $"READ:{operation.TagName}",
            WriteOperation => $"WRITE:{operation.TagName}",
            InitializeOperation => $"INIT:{operation.TagName}",
            _ => string.Empty
        };

        private async Task ProcessOperation(QueuedOperation operation, CancellationToken cancel)
        {
            try
            {
                switch (operation)
                {
                    case ReadOperation readOp:
                        await readOp.Tag.ReadAsync(cancel);
                        MarkActivity();
                        readOp.CompletionSource.TrySetResult(readOp.Tag);
                        break;

                    case InitializeOperation initOp:
                        if (!initOp.Tag.IsInitialized)
                            await initOp.Tag.InitializeAsync(cancel);
                        MarkActivity();
                        initOp.CompletionSource.TrySetResult(initOp.Tag);
                        break;

                    case WriteOperation writeOp:
                        await writeOp.Tag.WriteAsync(cancel);
                        MarkActivity();
                        writeOp.CompletionSource.TrySetResult(writeOp.Tag);
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

            foreach (var tagLock in perTagLocks.Values)
                tagLock.Dispose();
            perTagLocks.Clear();

            globalConcurrency.Dispose();
            consumerCts?.Dispose();
        }
    }
}