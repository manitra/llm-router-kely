using RouterKely.Core.Statistics;
using Xunit;

namespace RouterKely.Unit.Statistics;

public sealed class InMemoryStatisticsProviderTests
{
    private static DateTimeOffset UtcHour(DateOnly day, int hour) =>
        new(day.Year, day.Month, day.Day, hour, 0, 0, TimeSpan.Zero);

    private static UsageAggregate Entry(
        DateOnly day,
        long userId,
        long keyId,
        string model,
        long requests,
        long costNanoUsd) =>
        new(
            UtcHour(day, 12),
            userId,
            keyId,
            model,
            UsageOutcome.Success,
            requests,
            requests * 10,
            0,
            requests * 5,
            costNanoUsd,
            1,
            0);

    [Fact]
    public async Task UsageRowsKeepUserKeyAndModelAttribution()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        var provider = new InMemoryStatisticsProvider();
        await provider.WriteAsync(
            new UsageBatch(
            [
                Entry(today, 7, 42, "axian-fast", 2, 100),
                Entry(today, 7, 42, "axian-pro", 1, 500),
                Entry(today, 8, 43, "axian-fast", 3, 30)
            ]),
            CancellationToken.None);

        UsageRowsSnapshot snapshot = await provider.QueryUsageRowsAsync(
            DateOnly.MinValue,
            DateOnly.MaxValue,
            CancellationToken.None);

        Assert.Equal(3, snapshot.Rows.Length);
        UsageRow pro = snapshot.Rows.Single(row => row.ModelAlias == "axian-pro");
        Assert.Equal(today, pro.Date);
        Assert.Equal(7, pro.UserId);
        Assert.Equal(42, pro.KeyId);
        Assert.Equal(1, pro.RequestCount);
        Assert.Equal(10, pro.InputTokens);
        Assert.Equal(5, pro.OutputTokens);
        Assert.Equal(500, pro.CostNanoUsd);
    }

    [Fact]
    public async Task UsageRowsAreFilteredByTheRequestedDateRange()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly older = today.AddDays(-2);
        var provider = new InMemoryStatisticsProvider();
        await provider.WriteAsync(
            new UsageBatch(
            [
                Entry(older, 7, 42, "axian-fast", 1, 1),
                Entry(today, 7, 42, "axian-fast", 1, 2)
            ]),
            CancellationToken.None);

        UsageRowsSnapshot snapshot = await provider.QueryUsageRowsAsync(
            today,
            today,
            CancellationToken.None);

        UsageRow row = Assert.Single(snapshot.Rows);
        Assert.Equal(today, row.Date);
        Assert.Equal(2, row.CostNanoUsd);
    }

    [Fact]
    public async Task InstalledDailyRowsAreQueryableAndIdempotent()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var provider = new InMemoryStatisticsProvider();
        var snapshot = new UsageRowsSnapshot(
            [new UsageRow(day, 7, 42, "axian-fast", 4, 40, 0, 20, 400)]);

        provider.InstallDailyRows(snapshot);
        provider.InstallDailyRows(snapshot); // a second restore must not double the totals

        StatisticsSnapshot restored = await provider.QueryAsync(day, day, 7, 42, CancellationToken.None);
        DailyUsage usage = Assert.Single(restored.Days);
        Assert.Equal(4, usage.RequestCount);
        Assert.Equal(40, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(400, usage.CostNanoUsd);

        // A live aggregate for the same day merges into the installed cell instead of replacing it.
        await provider.WriteAsync(
            new UsageBatch([Entry(day, 7, 42, "axian-fast", 1, 10)]),
            CancellationToken.None);
        UsageRow merged = Assert.Single(
            (await provider.QueryUsageRowsAsync(day, day, CancellationToken.None)).Rows);
        Assert.Equal(5, merged.RequestCount);
        Assert.Equal(410, merged.CostNanoUsd);
    }
}
