using libplctag;
using Logix.Tags;

namespace Logix.Driver
{
    public class Driver : IDriver
    {
        public Target Target { get; }
        public bool IsConnected => monitor.IsConnected;
        public string ControllerInfo => monitor.ControllerInfo;

        private readonly ITagValueChannel channel;

        private readonly ITagCache tagCache;
        private readonly ITagMetaProvider metaProvider;
        private readonly ITagValueResolver valueResolver;
        private readonly ITagFactory tagFactory;
        private readonly ConnectionMonitor monitor;

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

            monitor = new ConnectionMonitor(
                target.HeartbeatInterval,
                // one timeout waiting behind in-flight ops for a slot, one for the probe itself
                TimeSpan.FromMilliseconds(target.TimeoutMs * 2),
                channel,
                () => tagFactory.Create("@raw"),
                connected => ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(connected)));
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

        public Task<bool> TryConnectAsync(CancellationToken token = default)
        {
            return monitor.ProbeNowAsync(token);
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
            if (!IsConnected)
                return null;

            var (definition, tag) = GetTagAsync(tagName).GetAwaiter().GetResult();

            try
            {
                var buffer = channel.Reader.ReadBuffer(tag);
                return valueResolver.ResolveValue(buffer, definition);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                {
                    monitor.RequestProbe();
                    return null;
                }
                else throw;
            }
        }

        public async Task<object?> ReadTagValueAsync(string tagName)
        {
            if (!IsConnected)
                return null;

            var (definition, tag) = await GetTagAsync(tagName);

            try
            {
                var buffer = await channel.Reader.ReadBufferAsync(tag);
                return valueResolver.ResolveValue(buffer, definition);
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                {
                    monitor.RequestProbe();
                    return null;
                }
                else throw;
            }
        }

        public void WriteTagValue(string tagName, object value)
        {
            if (!IsConnected)
                return;

            var (definition, tag) = GetTagAsync(tagName).GetAwaiter().GetResult();

            try
            {
                if (!tag.IsInitialized)
                    tag = channel.Writer.Initialize(tag);

                // encoding runs inside the queue under the per-tag lock, not against the shared tag here
                channel.Writer.WriteTag(tag, buffer => valueResolver.WriteTagBuffer(buffer, definition, value));
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                    monitor.RequestProbe();
                else throw;
            }
        }

        public async Task WriteTagValueAsync(string tagName, object value)
        {
            if (!IsConnected)
                return;

            var (definition, tag) = await GetTagAsync(tagName);

            try
            {
                if (!tag.IsInitialized)
                    tag = await channel.Writer.InitializeAsync(tag);

                // encoding runs inside the queue under the per-tag lock, not against the shared tag here
                await channel.Writer.WriteTagAsync(tag, buffer => valueResolver.WriteTagBuffer(buffer, definition, value));
            }
            catch (Exception ex)
            {
                if (CheckTagIsDisconnected(tag, ex.Message))
                    monitor.RequestProbe();
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

        private async Task<(TagDefinition, Tag)> GetTagAsync(string tagPath)
        {
            if (!metaProvider.TryGetTagDefinition(tagPath, out var definition) || definition!.ExpansionLevel != ExpansionLevel.Deep)
                definition = await metaProvider.LoadTagDefinitionAsync(tagPath);

            if (definition is null)
                throw new KeyNotFoundException($"Unable to load tag definition for {tagPath}.");

            var tag = tagCache.GetOrAdd(tagPath, () => definition.IsArray
                ? tagFactory.Create(ResolveArrayPath(tagPath, definition), definition.ElementCount())
                : tagFactory.Create(tagPath));

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

        public void Dispose()
        {
            monitor.Dispose();
            // drain in-flight ops before disposing the tags they're running on
            channel.Dispose();
            tagCache?.Flush();
        }
    }
}
