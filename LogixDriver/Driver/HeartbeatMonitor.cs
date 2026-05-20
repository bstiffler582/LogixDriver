namespace Logix.Driver
{
    /// <summary>
    /// Periodically checks an idle-since timestamp and fires a probe when the idle window
    /// exceeds the configured interval. On probe failure, invokes `onFailure` once and exits.
    /// Designed to be created when a connection comes up and disposed when it goes down.
    /// </summary>
    internal sealed class HeartbeatMonitor : IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

        private readonly TimeSpan heartbeatInterval;
        private readonly Func<long> getLastActivityTicks;
        private readonly Func<CancellationToken, Task<bool>> probe;
        private readonly Action onFailure;
        private readonly CancellationTokenSource cts = new();
        private readonly Task monitorTask;

        private volatile bool forceProbe;
        private bool disposed;

        public HeartbeatMonitor(
            TimeSpan heartbeatInterval,
            Func<long> getLastActivityTicks,
            Func<CancellationToken, Task<bool>> probe,
            Action onFailure)
        {
            if (heartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
            this.heartbeatInterval = heartbeatInterval;
            this.getLastActivityTicks = getLastActivityTicks;
            this.probe = probe;
            this.onFailure = onFailure;
            // Direct assignment (no Task.Run wrap) so monitorTask.Id matches Task.CurrentId inside the loop —
            // needed for the self-dispose re-entry check below.
            monitorTask = RunAsync(cts.Token);
        }

        /// <summary>
        /// Force the next tick to fire the probe regardless of recent activity.
        /// </summary>
        public void RequestProbe() => forceProbe = true;

        private async Task RunAsync(CancellationToken token)
        {
            var heartbeatMs = (long)heartbeatInterval.TotalMilliseconds;
            while (!token.IsCancellationRequested)
            {
                try 
                { 
                    await Task.Delay(CheckInterval, token); 
                }
                catch (OperationCanceledException) { return; }

                var idleMs = Environment.TickCount64 - getLastActivityTicks();
                if (!forceProbe && idleMs < heartbeatMs) 
                    continue;
                forceProbe = false;

                bool ok;
                try 
                { 
                    ok = await probe(token); 
                }
                catch (OperationCanceledException) 
                { 
                    return; 
                }
                catch 
                { 
                    ok = false; 
                }

                if (!ok)
                {
                    onFailure();
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cts.Cancel();

            // If Dispose is called from within the monitor loop (i.e., onFailure -> SetConnectionState
            // -> Driver disposes us synchronously), don't Wait on ourselves. The `return;` after
            // onFailure ensures the loop will exit on its own.
            if (Task.CurrentId != monitorTask.Id)
            {
                try { monitorTask.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { /* expected — OCE wrapped */ }
            }

            cts.Dispose();
        }
    }
}
