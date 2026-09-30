using Logix.Tags;

namespace Logix.Driver
{
    public class Driver : IDriver
    {
        public Target Target { get; }
        public bool IsConnected => monitor.IsConnected;
        public string ControllerInfo => monitor.ControllerInfo;
        public ITagDirectory Tags { get; }

        private readonly ITagChannel channel;
        private readonly ITagValueResolver valueResolver;
        private readonly ConnectionMonitor monitor;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public Driver(Target target, ITagValueResolver valueResolver, ITagDirectory tags, ITagChannel channel)
        {
            Target = target;
            Tags = tags;
            this.valueResolver = valueResolver;
            this.channel = channel;

            monitor = new ConnectionMonitor(
                target.HeartbeatInterval,
                // one timeout waiting behind in-flight ops for a slot, one for the probe itself
                TimeSpan.FromMilliseconds(target.TimeoutMs * 2),
                channel,
                connected => ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(connected)));
        }

        public static Driver Create(Target target, ITagValueResolver? valueResolver = null) =>
            Create(target, new TagFactory(target), valueResolver);

        internal static Driver Create(Target target, ITagFactory tagFactory, ITagValueResolver? valueResolver = null)
        {
            var channel = new TagChannel(tagFactory, target.MaxConcurrentOperations);
            return new Driver(target, valueResolver ?? new DefaultTagValueResolver(), new TagDirectory(channel), channel);
        }

        public Task<bool> TryConnectAsync(CancellationToken token = default) => monitor.ProbeNowAsync(token);

        public bool TryConnect() => TryConnectAsync().GetAwaiter().GetResult();

        public Task LoadTagsAsync(IEnumerable<string>? tagFilter = null) => Tags.LoadAsync(tagFilter);

        public void LoadTags(IEnumerable<string>? tagFilter = null) => LoadTagsAsync(tagFilter).GetAwaiter().GetResult();

        public object? ReadTagValue(string tagName) => ReadTagValueAsync(tagName).GetAwaiter().GetResult();

        public async Task<object?> ReadTagValueAsync(string tagName)
        {
            if (!IsConnected)
                return null;

            try
            {
                var resolved = await Tags.ResolveAsync(tagName).ConfigureAwait(false);
                var buffer = await channel.ReadAsync(resolved.NativeName, resolved.ElementCount).ConfigureAwait(false);
                return valueResolver.ResolveValue(buffer, resolved.Type, 0, resolved.BitOffset);
            }
            catch (OperationCanceledException)
            {
                // flushed (connection lost) or disposed while waiting to run
                return null;
            }
            catch (NativeTagException ex) when (ex.IsConnectionError)
            {
                monitor.RequestProbe();
                return null;
            }
        }

        public void WriteTagValue(string tagName, object value) => WriteTagValueAsync(tagName, value).GetAwaiter().GetResult();

        public async Task WriteTagValueAsync(string tagName, object value)
        {
            if (!IsConnected)
                return;

            try
            {
                var resolved = await Tags.ResolveAsync(tagName).ConfigureAwait(false);

                // The channel keys native tags by (name, element count), so every spelling of a path, and
                // all 32 bits of a BOOL array word, share one tag: concurrent writes to it are sent as one.
                // A BOOL array element only changes one bit of its word, so the word is re-read first.
                await channel.WriteAsync(
                    resolved.NativeName,
                    resolved.ElementCount,
                    buffer => valueResolver.WriteTagBuffer(buffer, resolved.Type, value, 0, resolved.BitOffset),
                    readModifyWrite: resolved.IsBitArrayElement).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // flushed (connection lost) or disposed while waiting to run
            }
            catch (NativeTagException ex) when (ex.IsConnectionError)
            {
                monitor.RequestProbe();
            }
        }

        public void Dispose()
        {
            monitor.Dispose();
            channel.Dispose();
        }
    }
}
