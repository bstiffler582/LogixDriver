namespace Logix.Tags
{
    /// <summary>
    /// Caps how many operations run against the controller at once. Waiters with high priority
    /// (writes, probes) are always served before normal ones (reads), so a heavy polling load
    /// can't hold writes back.
    /// </summary>
    internal sealed class PriorityLimiter
    {
        private readonly object sync = new();
        private readonly Queue<TaskCompletionSource> high = new();
        private readonly Queue<TaskCompletionSource> normal = new();
        private int available;

        public int Capacity { get; }

        public PriorityLimiter(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = available = capacity;
        }

        /// <summary>Waits for a slot; dispose the result to give it back.</summary>
        public async Task<Slot> AcquireAsync(bool highPriority, CancellationToken ct = default)
        {
            TaskCompletionSource waiter;
            lock (sync)
            {
                if (available > 0 && high.Count == 0 && normal.Count == 0)
                {
                    available--;
                    return new Slot(this);
                }

                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                (highPriority ? high : normal).Enqueue(waiter);
            }

            // A cancelled waiter stays queued; Release skips it. If Release grants the slot first,
            // TrySetCanceled loses the race and the slot is ours.
            using (ct.Register(() => waiter.TrySetCanceled(ct)))
                await waiter.Task.ConfigureAwait(false);

            return new Slot(this);
        }

        private void Release()
        {
            lock (sync)
            {
                while (high.Count > 0 || normal.Count > 0)
                {
                    var next = high.Count > 0 ? high.Dequeue() : normal.Dequeue();
                    if (next.TrySetResult())
                        return;
                }
                available++;
            }
        }

        public readonly struct Slot : IDisposable
        {
            private readonly PriorityLimiter limiter;
            internal Slot(PriorityLimiter limiter) => this.limiter = limiter;
            public void Dispose() => limiter.Release();
        }
    }
}
