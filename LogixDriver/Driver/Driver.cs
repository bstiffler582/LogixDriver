using libplctag;
using Logix.Tags;

namespace Logix.Driver
{
    public class Driver : IDriver
    {
        public Target Target { get; }
        public bool IsConnected => isConnected;
        public string ControllerInfo => controllerInfo;

        private readonly ITagValueChannel channel;

        private readonly ITagCache tagCache;
        private readonly ITagMetaProvider metaProvider;
        private readonly ITagValueResolver valueResolver;
        private readonly ITagFactory tagFactory;

        private volatile bool isConnected = false;
        private string controllerInfo = string.Empty;

        private HeartbeatMonitor? heartbeat;
        private readonly object stateLock = new();
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public Driver(
            Target target,
            ITagValueResolver valueResolver,
            ITagCache tagCache,
            ITagMetaProvider metaProvider,
            ITagValueChannel channel,
            ITagFactory tagFactory)
        {
            Target = target;
            this.valueResolver = valueResolver;
            this.tagCache = tagCache;
            this.metaProvider = metaProvider;
            this.channel = channel;
            this.tagFactory = tagFactory;
        }

        public static Driver Create(Target target, ITagValueResolver? valueResolver = null)
        {
            var tagFactory = new TagFactory(target);
            var channel = new TagValueChannelFactory().Open(tagFactory);
            return new Driver(
                target,
                valueResolver ?? new DefaultTagValueResolver(),
                new TagCache(),
                new TagMetaProvider(channel.Reader, new TagDefinitionCache()),
                channel,
                tagFactory
            );
        }

        public async Task<bool> TryConnectAsync(CancellationToken token = default)
        {
            if (isConnected)
                return true;

            string info = await ReadControllerInfoAsync(token);
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

        public bool TryConnect()
        {
            return TryConnectAsync().GetAwaiter().GetResult();
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
            if (!isConnected)
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
                    heartbeat?.RequestProbe();
                    return null;
                }
                else throw;
            }
        }

        public async Task<object?> ReadTagValueAsync(string tagName)
        {
            if (!isConnected)
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
                    heartbeat?.RequestProbe();
                    return null;
                }
                else throw;
            }
        }

        public void WriteTagValue(string tagName, object value)
        {
            if (!isConnected)
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
                    heartbeat?.RequestProbe();
                else throw;
            }
        }

        public async Task WriteTagValueAsync(string tagName, object value)
        {
            if (!isConnected)
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
                    heartbeat?.RequestProbe();
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

        private async Task<string> ReadControllerInfoAsync(CancellationToken token = default)
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
                    await channel.Writer.InitializeAsync(tag).WaitAsync(token);

                tag.SetSize(rawPayload.Length);
                tag.SetBuffer(rawPayload);

                await channel.Writer.WriteTagAsync(tag).WaitAsync(token);
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
            bool flush = false;
            HeartbeatMonitor? toDispose = null;

            lock (stateLock)
            {
                if (isConnected == connected) return;
                isConnected = connected;

                if (connected)
                {
                    if (Target.HeartbeatInterval > TimeSpan.Zero)
                        heartbeat = new HeartbeatMonitor(
                            Target.HeartbeatInterval,
                            () => channel.LastActivityAt,
                            async (ct) => !string.IsNullOrEmpty(await ReadControllerInfoAsync(ct)),
                            () => SetConnectionState(false));
                }
                else
                {
                    toDispose = heartbeat;
                    heartbeat = null;
                    flush = true;
                }
            }

            // Dispose the monitor and flush outside the state lock — both can block briefly and
            // may run continuations we don't want to hold the lock through.
            toDispose?.Dispose();
            if (flush) channel.Flush();
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(connected));
        }

        public void Dispose()
        {
            HeartbeatMonitor? toDispose;
            lock (stateLock)
            {
                isConnected = false;
                toDispose = heartbeat;
                heartbeat = null;
                tagCache?.Flush();
            }

            toDispose?.Dispose();
            channel.Dispose();
        }
    }
}
