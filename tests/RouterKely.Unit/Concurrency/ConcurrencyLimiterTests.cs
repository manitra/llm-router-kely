using RouterKely.Core.Concurrency;
using Xunit;

namespace RouterKely.Unit.Concurrency;

public sealed class ConcurrencyLimiterTests
{
    [Fact]
    public void RejectsWithoutQueueingAtLimitAndRecoversAfterRelease()
    {
        var limiter = new ConcurrencyLimiter(2);

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        Assert.Equal(2, limiter.ActiveCount);

        limiter.Release();

        Assert.True(limiter.TryAcquire());
        Assert.Equal(2, limiter.ActiveCount);
        limiter.Release();
        limiter.Release();
    }
}
