using libplctag;
using Logix.Tags;

namespace Logix.Driver
{
    public class Driver : IDriver
    {
        public Target Target { get; }
        public bool IsConnected => isConnected;
        public string ControllerInfo => controllerInfo;

        private ITagValueChannel? channel;

        private readonly ITagCache tagCache;
        private readonly ITagMetaProvider metaProvider;
        private readonly ITagValueResolver valueResolver;
        private readonly ITagValueChannelFactory channelFactory;
        private readonly ITagFactory tagFactory;

        private volatile bool isConnected = false;
        private string controllerInfo = string.Empty;

        private CancellationTokenSource? heartbeatCts;
        private Task? heartbeatTask;
        private readonly SemaphoreSlim heartbeatWake = new(0, 1);
        private readonly object stateLock = new();
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        private const uint QUEUE_INTERVAL_MS = 50;

        public Driver(
            Target target,
            ITagValueResolver valueResolver,
            ITagCache tagCache,
            ITagMetaProvider metaProvider,
            ITagValueChannelFactory channelFactory,
            ITagFactory tagFactory)
        {
            Target = target;
            this.valueResolver = valueResolver;
            this.tagCache = tagCache;
            this.metaProvider = metaProvider;
            this.channelFactory = channelFactory;
            this.tagFactory = tagFactory;
        }

        public static Driver Create(Target target, ITagValueResolver? valueResolver = null)
        {
            var tagFactory = new TagFactory(target);
            return new Driver(
                target,
                valueResolver ?? new DefaultTagValueResolver(),
                new TagCache(),
                new TagMetaProvider(tagFactory),
                new TagValueChannelFactory(),
                tagFactory
            );
        }

        public bool TryConnect()
        {
            if (isConnected)
                return true;

            string info = ReadControllerInfo();
            if (!string.IsNullOrEmpty(info))
            {
                controllerInfo = info;
                SetConnectionState(true);
                return true;
            }
            else
            {
                SetConnectionState(false);
                return false;
            }
        }

        public async Task LoadTagsAsync(IEnumerable<string>? tagFilter = null)
        {
            await metaProvider.LoadTagDefinitionsAsync(tagFilter);
        }

        public void LoadTags(IEnumerable<string>? tagFilter = null)
        {
            LoadTagsAsync(tagFilter).GetAwaiter().GetResult();
        }

        public IReadOnlyDictionary<string, TagDefinition> GetTagDefinitionsFlat()
        {
            return metaProvider.GetTagDefinitionsFlat();
        }

        public IEnumerable<TagDefinition> GetTagDefinitions()
        {
            return metaProvider.GetTagDefinitions();
        }

        public object? ReadTagValue(string tagName)
        {
            if (!isConnected || channel is null)
                return null;

            var (definition, tag) = GetTag(tagName);

            try
            {
                tag = channel.Reader.ReadTag(tag);
                return valueResolver.ResolveValue(tag, definition);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                {
                    NotifyDisconnect();
                    return null;
                }
                else throw;
            }
        }

        public async Task<object?> ReadTagValueAsync(string tagName)
        {
            if (!isConnected || channel is null)
                return null;

            var (definition, tag) = GetTag(tagName);

            try
            {
                tag = await channel.Reader.ReadTagAsync(tag);
                return valueResolver.ResolveValue(tag, definition);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                {
                    NotifyDisconnect();
                    return null;
                }
                else throw;
            }
        }

        public void WriteTagValue(string tagName, object value)
        {
            if (!isConnected || channel is null)
                return;

            var (definition, tag) = GetTag(tagName);

            try
            {
                if (!tag.IsInitialized)
                    tag = channel.Writer.Initialize(tag);

                valueResolver.WriteTagBuffer(tag, definition, value);
                tag = channel.Writer.WriteTag(tag);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                    NotifyDisconnect();
                else throw;
            }
        }

        public async Task WriteTagValueAsync(string tagName, object value)
        {
            if (!isConnected || channel is null)
                return;

            var (definition, tag) = GetTag(tagName);

            try
            {
                if (!tag.IsInitialized)
                    tag = await channel.Writer.InitializeAsync(tag);

                valueResolver.WriteTagBuffer(tag, definition, value);
                tag = await channel.Writer.WriteTagAsync(tag);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                    NotifyDisconnect();
                else throw;
            }
        }

        private static bool CheckTagIsDisconnected(Tag tag, string msg = "")
        {
            var status = tag.GetStatus() switch
            {
                Status.ErrorBadConnection => true,
                Status.ErrorTimeout => true,
                Status.ErrorWinsock => true,
                Status.Pending => true,
                _ => false
            };

            // observed condition where ErrorTimeout exception is thrown
            // when tag status is unrelated (e.g. NotFound)
            return (status || msg == "ErrorTimeout");
        }

        private string ReadControllerInfo(bool useChannel = false)
        {
            var rawPayload = new byte[] {
                0x01, 0x02, 0x20, 0x01, 0x24, 0x01 };

            if (!tagCache.TryGetTag("heartbeat", out var tag))
            {
                tag = tagFactory.Create("@raw");
                tagCache.AddTag("heartbeat", tag);
            }

            if (tag is null) return string.Empty;

            try
            {
                if (!tag.IsInitialized)
                    if (useChannel) 
                        channel!.Writer.Initialize(tag);
                    else 
                        tag.Initialize();

                tag.SetSize(rawPayload.Length);
                tag.SetBuffer(rawPayload);

                if (useChannel) 
                    channel!.Writer.WriteTag(tag);
                else 
                    tag.Write();

                return TagMetaDecoder.DecodeControllerInfo(tag);
            }
            catch (Exception)
            {
                if (CheckTagIsDisconnected(tag))
                    return string.Empty;
                else throw;
            }
        }

        private (TagDefinition, Tag) GetTag(string tagPath)
        {
            if (!metaProvider.TryGetTagDefinition(tagPath, out var definition) || definition!.ExpansionLevel != ExpansionLevel.Deep)
                definition = metaProvider.LoadTagDefinition(tagPath);

            if (definition is null)
                throw new KeyNotFoundException($"Unable to load tag definition for {tagPath}.");

            if (!tagCache.TryGetTag(tagPath, out var tag))
            {
                if (definition!.IsArray)
                {
                    var readPath = ResolveArrayPath(tagPath, definition);
                    tag = tagFactory.Create(readPath, definition.ElementCount());
                }
                else
                {
                    tag = tagFactory.Create(tagPath);
                }
                tagCache.AddTag(tagPath, tag);
            }

            if (tag is null)
                throw new Exception($"Unable to create tag {tagPath}.");

            return (definition, tag);
        }

        private static string ResolveArrayPath(string tagName, TagDefinition definition)
        {
            var member = definition;
            var path = tagName;
            while (member is not null && member.IsArray)
            {
                path += "[0]";
                member = member.Children?.First();
            }

            return path;
        }

        // connection state management
        private void SetConnectionState(bool connected)
        {
            ITagValueChannel? oldChannel = null;

            lock (stateLock)
            {
                if (isConnected == connected) return;
                isConnected = connected;

                if (connected)
                {
                    channel = channelFactory?.Open(tagFactory, QUEUE_INTERVAL_MS);
                    if (Target.HeartbeatInterval > TimeSpan.Zero)
                        StartHeartbeat();
                }
                else
                {
                    oldChannel = channel;
                    channel = null;
                    // The heartbeat task self-terminates after this call returns.
                    // Stale heartbeatCts/heartbeatTask refs are replaced on next StartHeartbeat
                    // and explicitly cleaned up in Dispose.
                }
            }

            oldChannel?.Dispose();
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(connected));
        }

        // Wake the heartbeat to re-probe immediately. Safe to call from any thread.
        private void NotifyDisconnect()
        {
            try { heartbeatWake.Release(); }
            catch (SemaphoreFullException) { /* already pending */ }
        }

        private void StartHeartbeat()
        {
            // drain any stale wake signal from a previous lifecycle
            while (heartbeatWake.Wait(0)) { }

            heartbeatCts = new CancellationTokenSource();
            var token = heartbeatCts.Token;
            var interval = Target.HeartbeatInterval;
            heartbeatTask = Task.Run(() => HeartbeatLoopAsync(interval, token));
        }

        private void StopHeartbeat()
        {
            var cts = heartbeatCts;
            var task = heartbeatTask;
            if (cts is null) return;

            cts.Cancel();
            try { task?.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* expected */ }
            cts.Dispose();
            heartbeatCts = null;
            heartbeatTask = null;
        }

        private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await heartbeatWake.WaitAsync(interval, token);

                    if (string.IsNullOrEmpty(ReadControllerInfo(useChannel: true)))
                    {
                        SetConnectionState(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            StopHeartbeat();

            lock (stateLock)
            {
                isConnected = false;
                tagCache?.Flush();
                channel?.Dispose();
                channel = null;
            }
        }
    }
}
