using libplctag;
using Logix.Tags;

namespace LogixDriver.Tests.Fakes
{
    /// <summary>
    /// In-memory INativeTag. Reads copy <see cref="PlcData"/> (the "controller side") into the tag
    /// buffer; writes copy the buffer back. Counts calls and can delay or fail operations.
    /// </summary>
    internal sealed class FakeNativeTag : INativeTag
    {
        private byte[] buffer = Array.Empty<byte>();
        private int inFlight;

        public FakeNativeTag(string name, byte[]? plcData = null)
        {
            Name = name;
            PlcData = plcData ?? Array.Empty<byte>();
        }

        public string Name { get; }
        public bool IsInitialized { get; private set; }
        public byte[] PlcData { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public Exception? FailWith { get; set; }
        public Status Status { get; set; } = Status.Ok;
        /// <summary>Replaces the tag buffer after a write, e.g. with a service response.</summary>
        public Func<byte[], byte[]>? OnWrite { get; set; }
        /// <summary>Optional counter shared between tags, to observe concurrency across them.</summary>
        public ConcurrencyCounter? Shared { get; set; }
        /// <summary>Called when an operation starts, with its kind ("init", "read", "write").</summary>
        public Action<string>? OnStart { get; set; }
        public bool IsDisposed { get; private set; }

        public int Reads;
        public int Writes;
        public int Initializes;
        /// <summary>Highest number of operations seen running on this handle at once.</summary>
        public int MaxConcurrentOps;

        public Task InitializeAsync(CancellationToken ct = default) => Run(ct, "init", () =>
        {
            Interlocked.Increment(ref Initializes);
            IsInitialized = true;
            if (buffer.Length == 0) buffer = (byte[])PlcData.Clone();
        });

        public Task ReadAsync(CancellationToken ct = default) => Run(ct, "read", () =>
        {
            Interlocked.Increment(ref Reads);
            IsInitialized = true;
            buffer = (byte[])PlcData.Clone();
        });

        public Task WriteAsync(CancellationToken ct = default) => Run(ct, "write", () =>
        {
            Interlocked.Increment(ref Writes);
            PlcData = (byte[])buffer.Clone();
            if (OnWrite is not null)
                buffer = OnWrite(buffer);
        });

        public byte[] GetBuffer() => (byte[])buffer.Clone();
        public void SetBuffer(byte[] data) => buffer = (byte[])data.Clone();
        public Status GetStatus() => Status;
        public void Dispose() => IsDisposed = true;

        private async Task Run(CancellationToken ct, string kind, Action op)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            OnStart?.Invoke(kind);
            var running = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref MaxConcurrentOps, running);
            Shared?.Enter();
            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, ct);
                else
                    await Task.Yield();

                if (FailWith is not null)
                    throw FailWith;
                op();
            }
            finally
            {
                Shared?.Exit();
                Interlocked.Decrement(ref inFlight);
            }
        }

        internal static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
        }
    }

    internal sealed class ConcurrencyCounter
    {
        private int current;
        public int Max;

        public void Enter() => FakeNativeTag.InterlockedMax(ref Max, Interlocked.Increment(ref current));
        public void Exit() => Interlocked.Decrement(ref current);
    }
}
