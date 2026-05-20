using libplctag;
using Logix.Tags;

namespace Logix.Driver
{
    /// <summary>
    /// Owns connection state for the Driver. Probes the controller on demand
    /// (TryConnect) and periodically (when an idle window passes). All transitions
    /// of <see cref="IsConnected"/> happen on this monitor's serialized probe path;
    /// callers can only request a probe, never mutate state directly.
    /// </summary>
    internal sealed class ConnectionMonitor : IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);
        private static readonly byte[] ProbePayload = { 0x01, 0x02, 0x20, 0x01, 0x24, 0x01 };

        private readonly TimeSpan heartbeatInterval;
        private readonly ITagValueChannel channel;
        private readonly Tag probeTag;
        private readonly Action<bool> onStateChanged;

        private readonly SemaphoreSlim probeGate = new(1, 1);
        private readonly CancellationTokenSource cts = new();
        private readonly Task? loopTask;

        private volatile bool isConnected;
        private volatile string controllerInfo = string.Empty;
        private volatile bool forceProbe;
        private bool disposed;

        public bool IsConnected => isConnected;
        public string ControllerInfo => controllerInfo;

        public ConnectionMonitor(
            TimeSpan heartbeatInterval,
            ITagValueChannel channel,
            Tag probeTag,
            Action<bool> onStateChanged)
        {
            this.heartbeatInterval = heartbeatInterval;
            this.channel = channel;
            this.probeTag = probeTag;
            this.onStateChanged = onStateChanged;

            if (heartbeatInterval > TimeSpan.Zero)
                loopTask = RunLoopAsync(cts.Token);
        }

        public Task<bool> ProbeNowAsync(CancellationToken ct = default) => RunProbeAsync(ct);

        public void RequestProbe() => forceProbe = true;

        private async Task RunLoopAsync(CancellationToken token)
        {
            var heartbeatMs = (long)heartbeatInterval.TotalMilliseconds;
            while (!token.IsCancellationRequested)
            {
                try 
                { 
                    await Task.Delay(CheckInterval, token); 
                }
                catch (OperationCanceledException) 
                { 
                    return; 
                }

                var idleMs = Environment.TickCount64 - channel.LastActivityAt;
                if (!forceProbe && idleMs < heartbeatMs) 
                    continue;

                forceProbe = false;

                await RunProbeAsync(token);
            }
        }

        private async Task<bool> RunProbeAsync(CancellationToken ct)
        {
            await probeGate.WaitAsync(ct);
            try
            {
                string info = await ReadControllerInfoAsync(ct);
                bool nowConnected = !string.IsNullOrEmpty(info);
                controllerInfo = info;

                if (nowConnected != isConnected)
                {
                    isConnected = nowConnected;
                    if (!nowConnected) 
                        channel.Flush();

                    onStateChanged(nowConnected);
                }
                return nowConnected;
            }
            finally { probeGate.Release(); }
        }

        private async Task<string> ReadControllerInfoAsync(CancellationToken ct)
        {
            try
            {
                if (!probeTag.IsInitialized)
                    await channel.Writer.InitializeAsync(probeTag).WaitAsync(ct);

                probeTag.SetSize(ProbePayload.Length);
                probeTag.SetBuffer(ProbePayload);

                await channel.Writer.WriteTagAsync(probeTag).WaitAsync(ct);
                return TagMetaDecoder.DecodeControllerInfo(probeTag);
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cts.Cancel();
            try { loopTask?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* expected — OCE wrapped */ }
            cts.Dispose();
            probeGate.Dispose();
        }
    }
}
