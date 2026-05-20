namespace Logix.Tags
{
    public interface ITagValueChannelFactory
    {
        ITagValueChannel Open(ITagFactory tagFactory, int maxConcurrency = 8);
    }

    public class TagValueChannelFactory : ITagValueChannelFactory
    {
        public ITagValueChannel Open(ITagFactory tagFactory, int maxConcurrency = 8)
        {
            var queue = new TagReadWriteQueue(maxConcurrency);
            return new TagValueChannel(
                new QueuedTagValueReader(tagFactory, queue),
                new QueuedTagValueWriter(queue),
                queue);
        }
    }
}
