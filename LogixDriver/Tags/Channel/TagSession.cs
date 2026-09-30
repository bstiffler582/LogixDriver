namespace Logix.Tags
{
    /// <summary>
    /// Owns one native tag and runs its operations one at a time, as libplctag requires.
    /// At most one read and one write are queued at once, and requests arriving while one is
    /// queued join it:
    /// <list type="bullet">
    /// <item>Reads share the queued read and its result.</item>
    /// <item>Writes add their encoder to the queued write. Encoders are applied in order to one
    /// buffer and sent in one write, so the last full value wins and partial changes (e.g. single
    /// BOOL array bits) all land in one read-modify-write.</item>
    /// </list>
    /// A queued write runs before a queued read.
    /// </summary>
    internal sealed class TagSession : IDisposable
    {
        private readonly INativeTag tag;
        private readonly TagChannel channel;
        private readonly object sync = new();
        private PendingRead? queuedRead;
        private PendingWrite? queuedWrite;
        private bool running;

        public TagSession(INativeTag tag, TagChannel channel)
        {
            this.tag = tag;
            this.channel = channel;
        }

        /// <summary>Reads the tag. The returned buffer is shared with joined readers: treat it as read-only.</summary>
        public Task<byte[]> ReadAsync()
        {
            lock (sync)
            {
                if (queuedRead is null)
                {
                    queuedRead = new PendingRead(channel.FlushToken);
                    StartIfIdle();
                }
                return queuedRead.Completion.Task;
            }
        }

        /// <param name="encode">fills the value into a copy of the tag's buffer</param>
        /// <param name="readModifyWrite">read the tag first, so encode starts from current data</param>
        public Task WriteAsync(Action<byte[]> encode, bool readModifyWrite)
        {
            lock (sync)
            {
                if (queuedWrite is null)
                {
                    queuedWrite = new PendingWrite(channel.FlushToken);
                    StartIfIdle();
                }
                return queuedWrite.Add(encode, readModifyWrite);
            }
        }

        public void Dispose() => tag.Dispose();

        // caller holds sync
        private void StartIfIdle()
        {
            if (running) return;
            running = true;
            // off the caller's thread: the caller is inside the lock
            _ = Task.Run(RunQueuedAsync);
        }

        private async Task RunQueuedAsync()
        {
            while (true)
            {
                PendingOperation? next;
                lock (sync)
                {
                    next = (PendingOperation?)queuedWrite ?? queuedRead;
                    if (next == queuedWrite) queuedWrite = null;
                    else queuedRead = null;

                    if (next is null)
                    {
                        running = false;
                        return;
                    }
                }

                await RunAsync(next).ConfigureAwait(false);
            }
        }

        private async Task RunAsync(PendingOperation operation)
        {
            try
            {
                // a flush (connection lost) cancels operations still waiting for a slot; once the
                // native operation starts it runs to completion, bounded by the tag's timeout
                using var slot = await channel.Limiter.AcquireAsync(operation is PendingWrite, operation.FlushToken).ConfigureAwait(false);

                if (!tag.IsInitialized)
                    await channel.RunNativeAsync(tag, tag.InitializeAsync).ConfigureAwait(false);

                switch (operation)
                {
                    case PendingRead read:
                        await channel.RunNativeAsync(tag, tag.ReadAsync).ConfigureAwait(false);
                        read.Completion.TrySetResult(tag.GetBuffer());
                        break;

                    case PendingWrite write:
                        if (write.ReadModifyWrite)
                            await channel.RunNativeAsync(tag, tag.ReadAsync).ConfigureAwait(false);

                        // start from the current buffer so bytes the encoders don't touch
                        // (other bits of a word, host bits of packed BOOLs, padding) keep their values
                        var buffer = tag.GetBuffer();
                        var encoded = write.Encode(buffer);
                        if (encoded.Count == 0)
                            break;

                        tag.SetBuffer(buffer);
                        await channel.RunNativeAsync(tag, tag.WriteAsync).ConfigureAwait(false);
                        foreach (var completion in encoded)
                            completion.TrySetResult();
                        break;
                }
            }
            catch (OperationCanceledException) when (operation.FlushToken.IsCancellationRequested)
            {
                operation.Cancel();
            }
            catch (Exception ex)
            {
                operation.Fail(ex);
            }
        }

        private abstract class PendingOperation
        {
            protected PendingOperation(CancellationToken flushToken) => FlushToken = flushToken;
            public CancellationToken FlushToken { get; }
            public abstract void Cancel();
            public abstract void Fail(Exception ex);
        }

        private sealed class PendingRead : PendingOperation
        {
            public PendingRead(CancellationToken flushToken) : base(flushToken) { }
            public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override void Cancel() => Completion.TrySetCanceled(FlushToken);
            public override void Fail(Exception ex) => Completion.TrySetException(ex);
        }

        private sealed class PendingWrite : PendingOperation
        {
            private readonly List<(Action<byte[]> Encode, TaskCompletionSource Completion)> encoders = new();

            public PendingWrite(CancellationToken flushToken) : base(flushToken) { }
            public bool ReadModifyWrite { get; private set; }

            // caller holds the session lock
            public Task Add(Action<byte[]> encode, bool readModifyWrite)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                encoders.Add((encode, completion));
                ReadModifyWrite |= readModifyWrite;
                return completion.Task;
            }

            /// <summary>
            /// Applies the encoders in order. One that throws (e.g. an invalid value) fails only its
            /// own caller, and the buffer is rolled back to before it. Returns the completions of
            /// the encoders that were applied.
            /// </summary>
            public List<TaskCompletionSource> Encode(byte[] buffer)
            {
                var applied = new List<TaskCompletionSource>(encoders.Count);
                var checkpoint = new byte[buffer.Length];
                foreach (var (encode, completion) in encoders)
                {
                    buffer.CopyTo(checkpoint, 0);
                    try
                    {
                        encode(buffer);
                        applied.Add(completion);
                    }
                    catch (Exception ex)
                    {
                        checkpoint.CopyTo(buffer, 0);
                        completion.TrySetException(ex);
                    }
                }
                return applied;
            }

            public override void Cancel()
            {
                foreach (var (_, completion) in encoders)
                    completion.TrySetCanceled(FlushToken);
            }

            public override void Fail(Exception ex)
            {
                foreach (var (_, completion) in encoders)
                    completion.TrySetException(ex);
            }
        }
    }
}
