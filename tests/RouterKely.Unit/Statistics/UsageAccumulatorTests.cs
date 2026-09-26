using RouterKely.Core.Identity;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;
using Xunit;

namespace RouterKely.Unit.Statistics;

public sealed class UsageAccumulatorTests
{
    [Fact]
    public void KeysForTheSameUserShareTheConcurrencyLimit()
    {
        var route = new ModelRoute(0, "deepseek-fast", "deepseek-chat");
        var identities = new IdentitySnapshot(
            1,
            [new IdentityUser(7, "User", "user@example.com", IdentityRole.User, true, null)],
            [
                new IdentityKey(41, 7, "First", new string('0', 64), "sk-rk_first", "first", true),
                new IdentityKey(42, 7, "Second", new string('1', 64), "sk-rk_second", "second", true)
            ]);
        var accumulator = new UsageAccumulator([route], identities, maxConcurrentRequestsPerUser: 1);

        Assert.True(accumulator.TryGetAccount(41, out UsageAccount? first));
        Assert.True(accumulator.TryGetAccount(42, out UsageAccount? second));
        Assert.Same(first!.Concurrency, second!.Concurrency);
        Assert.True(first.Concurrency.TryAcquire());
        Assert.False(second.Concurrency.TryAcquire());
        first.Concurrency.Release();
        Assert.True(second.Concurrency.TryAcquire());
        second.Concurrency.Release();
    }

    [Fact]
    public async Task ExchangeFeedsInMemoryProviderAndUpdatesQuota()
    {
        var route = new ModelRoute(0, "deepseek-fast", "deepseek-chat");
        var identities = new IdentitySnapshot(
            1,
            [new IdentityUser(7, "User", "user@example.com", IdentityRole.User, true, 1_000)],
            [new IdentityKey(42, 7, "Key", new string('0', 64), "sk-rk_test", "test", true)]);
        var accumulator = new UsageAccumulator([route], identities);
        Assert.True(accumulator.TryGetAccount(42, out UsageAccount? account));
        var provider = new InMemoryStatisticsProvider();
        var usage = new UsageObservation(100, 20, 30, true);

        account!.Record(route, UsageOutcome.Success, usage, 250, 10);
        UsageBatch batch = accumulator.ExchangePending(DateTimeOffset.UtcNow);
        await provider.WriteAsync(batch, CancellationToken.None);
        StatisticsSnapshot snapshot = await provider.QueryAsync(
            DateOnly.FromDateTime(DateTime.UtcNow),
            DateOnly.FromDateTime(DateTime.UtcNow),
            7,
            42,
            CancellationToken.None);

        Assert.Equal(250, account.Quota.CurrentUsageNanoUsd);
        Assert.False(account.Quota.IsExceeded);
        DailyUsage day = Assert.Single(snapshot.Days);
        Assert.Equal(1, day.RequestCount);
        Assert.Equal(100, day.InputTokens);
        Assert.Equal(30, day.OutputTokens);
        Assert.Equal(250, day.CostNanoUsd);
        Assert.Empty(accumulator.ExchangePending(DateTimeOffset.UtcNow).Entries);

        account.Record(route, UsageOutcome.Success, usage, 750, 10);
        Assert.True(account.Quota.IsExceeded);
    }
}
