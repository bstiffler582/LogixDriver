using Logix.Tags;

namespace Logix.Driver
{
    public interface IDriver : IDisposable
    {
        /// <summary>
        /// Reads the controller tag list and fully resolves the types of the given paths, for browsing
        /// (see <see cref="ITagDirectory.LoadAsync"/>). Not needed before reading or writing: any path
        /// resolves on first use.
        /// </summary>
        public Task LoadTagsAsync(IEnumerable<string>? tagFilter = null);
        public void LoadTags(IEnumerable<string>? tagFilter = null);
        /// <summary>The controller's tags, programs and types, for browsing and path resolution.</summary>
        public ITagDirectory Tags { get; }
        public Target Target { get; }
        public bool IsConnected { get; }
        public string ControllerInfo { get; }
        public object? ReadTagValue(string tagName);
        public Task<object?> ReadTagValueAsync(string tagName);
        public void WriteTagValue(string tagName, object value);
        public Task WriteTagValueAsync(string tagName, object value);
        /// <summary>
        /// Probes the controller now and starts background connection monitoring (if not already
        /// running): heartbeat probes while connected, automatic reconnect with backoff while
        /// disconnected. Subscribe to <see cref="ConnectionStateChanged"/> before the first call.
        /// </summary>
        public Task<bool> TryConnectAsync(CancellationToken token = default);
        public bool TryConnect();
        /// <summary>
        /// Raised on the connection monitor's probe path, in order. Handlers should return quickly
        /// and must not block on TryConnect/TryConnectAsync (the probe path is waiting on them).
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    }

    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public ConnectionStateChangedEventArgs(bool isConnected) => IsConnected = isConnected;
    }
}
