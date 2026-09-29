using Logix.Tags;

namespace LogixDriver.Tests
{
    public class PriorityLimiterTests
    {
        [Fact]
        public async Task HighPriorityWaitersAreServedFirst()
        {
            var limiter = new PriorityLimiter(1);
            var held = await limiter.AcquireAsync(false);
            var order = new List<string>();

            var normal = Take("normal", false);
            var high = Take("high", true);
            held.Dispose();
            await Task.WhenAll(normal, high);

            Assert.Equal(new[] { "high", "normal" }, order);

            async Task Take(string name, bool highPriority)
            {
                using var slot = await limiter.AcquireAsync(highPriority);
                lock (order) order.Add(name);
            }
        }

        [Fact]
        public async Task CancelledWaitersDontLeakSlots()
        {
            var limiter = new PriorityLimiter(1);
            var held = await limiter.AcquireAsync(false);

            using var cts = new CancellationTokenSource();
            var cancelled = limiter.AcquireAsync(false, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            held.Dispose();
            using var next = await limiter.AcquireAsync(false).WaitAsync(TimeSpan.FromSeconds(1));
        }
    }
}
