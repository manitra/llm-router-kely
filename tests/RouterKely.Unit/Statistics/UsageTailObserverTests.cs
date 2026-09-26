using RouterKely.Core.Statistics;
using Xunit;

namespace RouterKely.Unit.Statistics;

public sealed class UsageStreamObserverTests
{
    [Fact]
    public void ReadFindsDeepSeekUsageAcrossAppends()
    {
        var observer = new UsageStreamObserver();
        observer.Append("data: {\"choices\":[]"u8);
        observer.Append(",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,"u8);
        observer.Append("\"prompt_cache_hit_tokens\":40}}\n\n"u8);

        UsageObservation usage = observer.Read();

        Assert.True(usage.Found);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(40, usage.CachedInputTokens);
        Assert.Equal(20, usage.OutputTokens);
    }

    [Fact]
    public void ReadReturnsMissingWhenNoUsageExists()
    {
        var observer = new UsageStreamObserver();
        observer.Append("data: {\"choices\":[]}\n\n"u8);

        Assert.False(observer.Read().Found);
    }

    [Fact]
    public void ReadWorksAcrossEveryByteBoundaryAndIgnoresUsageTextInsideContent()
    {
        byte[] payload = "data: {\"choices\":[{\"delta\":{\"content\":\"\\\"usage\\\":{\\\"prompt_tokens\\\":999}\"}}]}\n\ndata: {\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"prompt_cache_hit_tokens\":2}}\n\n"u8.ToArray();

        for (int split = 0; split <= payload.Length; split++)
        {
            var observer = new UsageStreamObserver();
            observer.Append(payload.AsSpan(0, split));
            observer.Append(payload.AsSpan(split));

            UsageObservation usage = observer.Read();
            Assert.True(usage.Found);
            Assert.Equal(12, usage.InputTokens);
            Assert.Equal(2, usage.CachedInputTokens);
            Assert.Equal(3, usage.OutputTokens);
        }
    }
}
