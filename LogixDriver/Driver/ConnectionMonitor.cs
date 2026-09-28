using libplctag;
using Logix.Tags;

namespace Logix.Driver
{
    /// <summary>
    /// Owns connection state for the Driver. Probes the controller on demand (TryConnect) and,
    /// once started, in the background: heartbeat probes while connected and idle, and reconnect
    /// attempts with backoff while disconnected. All transitions of <see cref="IsConnected"/>
    /// happen on this monitor's serialized probe path; callers can only request a probe.
    /// </summary>
    internal sealed class ConnectionMonitor : IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);
        // Delay before each reconnect attempt while disconnected; the last entry repeats.
        private static readonly TimeSpan[] ReconnectBackoff =
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        };
        private static readonly byte[] ProbePayload = { 0x01, 0x02, 0x20, 0x01, 0x24, 0x01 };

        private readonly TimeSpan heartbeatInterval;
        private readonly TimeSpan probeTimeout;
        private readonly ITagValueChannel channel;
        private readonly Func<Tag> probeTagFactory;
        private readonly Action<bool> onStateChanged;

        private readonly SemaphoreSlim probeGate = new(1, 1);
        private readonly CancellationTokenSource cts = new();
        private readonly object loopLock = new();
        private Task? loopTask;
        private bool disposed;

        // guarded by probeGate
        private Tag? probeTag;
        private int failedAttempts;

        // Environment.TickCount64 of the next reconnect attempt; written under probeGate, read by the loop
        private long nextAttemptAt;

        private volatile bool isConnected;
        private volatile string controllerInfo = string.Empty;
        private volatile bool forceProbe;

        public bool IsConnected => isConnected;
        public string ControllerInfo => controllerInfo;

        public ConnectionMonitor(
            TimeSpan heartbeatInterval,
            TimeSpan probeTimeout,
            ITagValueChannel channel,
            Func<Tag> probeTagFactory,
            Action<bool> onStateChanged)
        {
            this.heartbeatInterval = heartbeatInterval;
            this.probeTimeout = probeTimeout;
            this.channel = channel;
            this.probeTagFactory = probeTagFactory;
            this.onStateChanged = onStateChanged;
        }

        /// <summary>
        /// Probes immediately, then starts background monitoring if it isn't running yet.
        /// Monitoring starts here rather than in the constructor so callers can subscribe to
        /// state changes before any transition can happen.
        /// </summary>
        public async Task<bool> ProbeNowAsync(CancellationToken ct = default)
        {
            var connected = await RunProbeAsync(ct);
            EnsureLoopStarted();
            return connected;
        }

        /// <summary>Requests a probe on the next check, e.g. after a read/write failed. No-op while disconnected.</summary>
        public void RequestProbe() => forceProbe = true;

        private void EnsureLoopStarted()
        {
            lock (loopLock)
            {
                if (loopTask is null && !disposed)
                    loopTask = RunLoopAsync(cts.Token);
            }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CheckInterval, token);

                    if (IsProbeDue())
                        await RunProbeAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // never let one bad iteration end monitoring — IsConnected would go stale for good
                    Console.WriteLine($"Connection monitor error: {ex}");
                }
            }
        }

        private bool IsProbeDue()
        {
            var now = Environment.TickCount64;

            if (!isConnected)
                return now >= Volatile.Read(ref nextAttemptAt);

            if (forceProbe)
                return true;

            return heartbeatInterval > TimeSpan.Zero
                && now - channel.LastActivityAt >= (long)heartbeatInterval.TotalMilliseconds;
        }

        private async Task<bool> RunProbeAsync(CancellationToken ct)
        {
            await probeGate.WaitAsync(ct);
            try
            {
                forceProbe = false;

                string info = await ReadControllerInfoAsync(ct);
                bool nowConnected = !string.IsNullOrEmpty(info);
                controllerInfo = info;

                if (nowConnected)
                {
                    failedAttempts = 0;
                }
                else
                {
                    var delay = ReconnectBackoff[Math.Min(failedAttempts, ReconnectBackoff.Length - 1)];
                    failedAttempts++;
                    Volatile.Write(ref nextAttemptAt, Environment.TickCount64 + (long)delay.TotalMilliseconds);
                }

                if (nowConnected != isConnected)
                {
                    isConnected = nowConnected;
                    if (!nowConnected)
                        channel.Flush();

                    NotifyStateChanged(nowConnected);
                }
                return nowConnected;
            }
            finally { probeGate.Release(); }
        }

        // Invoked on the probe path (under probeGate) so notifications are delivered in order.
        // A throwing handler must not break the probe path or the loop.
        private void NotifyStateChanged(bool connected)
        {
            try
            {
                onStateChanged(connected);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ConnectionStateChanged handler threw: {ex}");
            }
        }

        private async Task<string> ReadControllerInfoAsync(CancellationToken ct)
        {
            Tag? tag = null;
            Task? probe = null;
            try
            {
                tag = probeTag ??= probeTagFactory();
                probe = WriteProbeAsync(tag);

                // bounded so a hung op can't stall monitoring; the op itself is still bounded by the tag timeout
                await probe.WaitAsync(probeTimeout, ct);
                return TagMetaDecoder.DecodeControllerInfo(tag);
            }
            catch (Exception ex)
            {
                // Don't reuse a tag left failed or half-initialized; the next probe starts from a fresh one.
                // Dispose only after its queued op has finished, since it may still be in flight after a timeout.
                probeTag = null;
                if (tag is not null)
                {
                    if (probe is null)
                        tag.Dispose();
                    else
                        _ = probe.ContinueWith(_ => tag.Dispose(), TaskScheduler.Default);
                }

                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                    throw;

                return string.Empty;
            }
        }

        private async Task WriteProbeAsync(Tag tag)
        {
            if (!tag.IsInitialized)
                await channel.Writer.InitializeAsync(tag);

            // Set outside the queue: only the monitor uses this tag, and probes are serialized by probeGate.
            tag.SetSize(ProbePayload.Length);
            tag.SetBuffer(ProbePayload);

            await channel.Writer.WriteTagAsync(tag);
        }

        public void Dispose()
        {
            Task? loop;
            lock (loopLock)
            {
                if (disposed) return;
                disposed = true;
                loop = loopTask;
            }

            cts.Cancel();
            try { loop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* expected — OCE wrapped */ }

            probeTag?.Dispose();
            probeTag = null;

            cts.Dispose();
            probeGate.Dispose();
        }
    }
}
