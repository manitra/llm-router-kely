using System.Text;
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

    [Fact]
    public void ReadSkipsLongContentStringsAndStillFindsUsage()
    {
        // The content string is long enough to span several read chunks with no quote or backslash
        // in them, which is exactly what the scan jumps over instead of walking byte by byte.
        byte[] payload = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\""
            + new string('a', 40_000)
            + "\"}}]}\n\ndata: {\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":9,\"prompt_cache_hit_tokens\":3}}\n\n");

        var observer = new UsageStreamObserver();
        for (int offset = 0; offset < payload.Length; offset += 4_096)
            observer.Append(payload.AsSpan(offset, Math.Min(4_096, payload.Length - offset)));

        UsageObservation usage = observer.Read();

        Assert.True(usage.Found);
        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(3, usage.CachedInputTokens);
        Assert.Equal(9, usage.OutputTokens);
    }
}
