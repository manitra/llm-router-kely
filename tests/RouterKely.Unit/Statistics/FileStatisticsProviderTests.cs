using System.Globalization;
using Microsoft.Extensions.Logging;
using RouterKely.Core.Statistics;
using RouterKely.Statistics;
using Xunit;

namespace RouterKely.Unit.Statistics;

public sealed class FileStatisticsProviderTests
{
    [Fact]
    public async Task WritePersistsTodayAndLoadRestoresItForQuotaSeeding()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store();
        await store.Provider.WriteAsync(
            new UsageBatch(
            [
                Entry(today, 7, 42, "axian-fast", 4, 400),
                Entry(today, 8, 43, "axian-pro", 1, 100)
            ]),
            CancellationToken.None);

        string file = Assert.Single(Directory.GetFiles(store.Root, "*.json"));
        Assert.Equal(DayFileName(today), Path.GetFileName(file));

        // A fresh provider, as after a restart, restores the rows and the current-day cost per user.
        var inner = new InMemoryStatisticsProvider(3, 7);
        var restarted = new FileStatisticsProvider(inner, store.Root, 7, store.Logger);
        IReadOnlyDictionary<long, long> costByUser = await restarted.LoadAsync(CancellationToken.None);

        Assert.Equal(400, costByUser[7]);
        Assert.Equal(100, costByUser[8]);

        UsageRowsSnapshot rows = await inner.QueryUsageRowsAsync(today, today, CancellationToken.None);
        Assert.Equal(2, rows.Rows.Length);
        UsageRow restored = rows.Rows.Single(row => row.UserId == 7);
        Assert.Equal(42, restored.KeyId);
        Assert.Equal(4, restored.RequestCount);
        Assert.Equal(40, restored.InputTokens);
        Assert.Equal(20, restored.OutputTokens);
        Assert.Equal(400, restored.CostNanoUsd);
    }

    [Fact]
    public async Task OlderDaysAreRestoredButNotSeededIntoTheCurrentDayQuota()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly yesterday = today.AddDays(-1);
        using var store = new Store();
        await store.Provider.WriteAsync(
            new UsageBatch([Entry(yesterday, 7, 42, "axian-fast", 2, 200)]),
            CancellationToken.None);

        var inner = new InMemoryStatisticsProvider(3, 7);
        var restarted = new FileStatisticsProvider(inner, store.Root, 7, store.Logger);
        IReadOnlyDictionary<long, long> costByUser = await restarted.LoadAsync(CancellationToken.None);

        Assert.Empty(costByUser); // yesterday's spend must not be charged to today's quota
        UsageRowsSnapshot rows = await inner.QueryUsageRowsAsync(yesterday, yesterday, CancellationToken.None);
        Assert.Equal(200, Assert.Single(rows.Rows).CostNanoUsd);
    }

    [Fact]
    public async Task WritePrunesExpiredDayFilesAndAbandonedTempFilesButKeepsForeignFiles()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store(retentionDays: 3);
        string stale = Path.Combine(store.Root, "2020-01-01.json");
        string abandoned = Path.Combine(store.Root, "2020-01-02.json.tmp");
        string foreign = Path.Combine(store.Root, "notes.txt");
        await File.WriteAllTextAsync(stale, "{\"version\":1,\"date\":\"2020-01-01\",\"rows\":[]}");
        await File.WriteAllTextAsync(abandoned, "partial");
        await File.WriteAllTextAsync(foreign, "keep me");

        await store.Provider.WriteAsync(
            new UsageBatch([Entry(today, 7, 42, "axian-fast", 1, 5)]),
            CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(Path.Combine(store.Root, DayFileName(today))));
    }

    [Fact]
    public async Task WriteLeavesNoTemporaryFileBehind()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store();
        await store.Provider.WriteAsync(
            new UsageBatch([Entry(today, 7, 42, "axian-fast", 1, 5)]),
            CancellationToken.None);

        Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
    }

    [Fact]
    public async Task LoadIgnoresForeignFilesAndCreatesAMissingDirectory()
    {
        using var store = new Store();
        Directory.Delete(store.Root); // a first start has no store directory at all

        Assert.Empty(await store.Provider.LoadAsync(CancellationToken.None));
        Assert.True(Directory.Exists(store.Root));

        await File.WriteAllTextAsync(Path.Combine(store.Root, "notes.txt"), "not a usage file");
        await File.WriteAllTextAsync(Path.Combine(store.Root, "readme.json"), "{}");

        Assert.Empty(await store.Provider.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadFailsWithThePathAndRemedyWhenADayFileIsCorrupt()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store();
        string path = Path.Combine(store.Root, DayFileName(today));
        await File.WriteAllTextAsync(path, "{ this is not json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Provider.LoadAsync(CancellationToken.None));

        Assert.Contains(path, exception.Message);
        Assert.Contains("delete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadRejectsAFileWhoseRecordedDateDisagreesWithItsName()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store();
        await File.WriteAllTextAsync(
            Path.Combine(store.Root, DayFileName(today)),
            "{\"version\":1,\"date\":\"1999-01-01\",\"rows\":[]}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.Provider.LoadAsync(CancellationToken.None));

        Assert.Contains("records date", exception.Message);
    }

    [Fact]
    public async Task AWriteFailureIsReportedOnceAndNeverFailsTheFlush()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var store = new Store();
        // A directory in place of the day file makes the atomic rename fail.
        Directory.CreateDirectory(Path.Combine(store.Root, DayFileName(today)));

        await store.Provider.WriteAsync(
            new UsageBatch([Entry(today, 7, 42, "axian-fast", 1, 5)]),
            CancellationToken.None);
        await store.Provider.WriteAsync(
            new UsageBatch([Entry(today, 7, 42, "axian-fast", 1, 7)]),
            CancellationToken.None);

        Assert.Contains(store.Root, Assert.Single(store.Logger.Errors));
        // The in-memory provider still received both aggregates, so inference accounting is intact.
        StatisticsSnapshot snapshot = await store.Inner.QueryAsync(today, today, 7, 42, CancellationToken.None);
        Assert.Equal(2, Assert.Single(snapshot.Days).RequestCount);
    }

    private static string DayFileName(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";

    private static UsageAggregate Entry(
        DateOnly day,
        long userId,
        long keyId,
        string model,
        long requests,
        long costNanoUsd) =>
        new(
            new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.Zero),
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

    private sealed class Store : IDisposable
    {
        public Store(int retentionDays = 7)
        {
            Root = TestPaths.CreateTempDirectory("usage");
            Provider = new FileStatisticsProvider(Inner, Root, retentionDays, Logger);
        }

        public string Root { get; }

        public TestLogger Logger { get; } = new();

        public InMemoryStatisticsProvider Inner { get; } = new(3, 7);

        public FileStatisticsProvider Provider { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}
