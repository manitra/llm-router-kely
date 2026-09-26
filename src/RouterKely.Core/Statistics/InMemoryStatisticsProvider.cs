namespace RouterKely.Core.Statistics;

public sealed class InMemoryStatisticsProvider : IStatisticsProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<DailyKey, MutableUsage> _daily = [];
    private readonly Dictionary<HourlyKey, MutableUsage> _hourly = [];
    private readonly int _hourlyRetentionHours;
    private readonly int _dailyRetentionDays;

    public InMemoryStatisticsProvider(int hourlyRetentionHours = 72, int dailyRetentionDays = 7)
    {
        if (hourlyRetentionHours is < 1 or > 168)
            throw new ArgumentOutOfRangeException(nameof(hourlyRetentionHours));
        if (dailyRetentionDays is < 1 or > 31)
            throw new ArgumentOutOfRangeException(nameof(dailyRetentionDays));

        _hourlyRetentionHours = hourlyRetentionHours;
        _dailyRetentionDays = dailyRetentionDays;
    }

    public ValueTask<StatisticsSnapshot> RestoreAsync(CancellationToken cancellationToken) =>
        QueryAsync(DateOnly.MinValue, DateOnly.MaxValue, null, null, cancellationToken);

    public ValueTask WriteAsync(UsageBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (UsageAggregate entry in batch.Entries)
            {
                Merge(_hourly, new HourlyKey(entry.PeriodHour, entry.UserId, entry.KeyId, entry.ModelAlias), entry);
                Merge(_daily, new DailyKey(DateOnly.FromDateTime(entry.PeriodHour.UtcDateTime), entry.UserId, entry.KeyId, entry.ModelAlias), entry);
            }

            PurgeExpired(DateTimeOffset.UtcNow);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<StatisticsSnapshot> QueryAsync(
        DateOnly startDate,
        DateOnly endDate,
        long? userId,
        long? keyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DailyUsage[] days = _daily
                .Where(pair =>
                    pair.Key.Date >= startDate && pair.Key.Date <= endDate &&
                    (userId is null || pair.Key.UserId == userId) &&
                    (keyId is null || pair.Key.KeyId == keyId))
                .GroupBy(pair => pair.Key.Date)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    ModelDailyUsage[] models = group
                        .GroupBy(pair => pair.Key.ModelAlias, StringComparer.Ordinal)
                        .Select(modelGroup => new ModelDailyUsage(
                            modelGroup.Key,
                            modelGroup.Sum(pair => pair.Value.RequestCount),
                            modelGroup.Sum(pair => pair.Value.InputTokens),
                            modelGroup.Sum(pair => pair.Value.CachedInputTokens),
                            modelGroup.Sum(pair => pair.Value.OutputTokens),
                            modelGroup.Sum(pair => pair.Value.CostNanoUsd)))
                        .OrderBy(model => model.ModelAlias, StringComparer.Ordinal)
                        .ToArray();

                    return new DailyUsage(
                        group.Key,
                        models.Sum(model => model.RequestCount),
                        models.Sum(model => model.InputTokens),
                        models.Sum(model => model.CachedInputTokens),
                        models.Sum(model => model.OutputTokens),
                        models.Sum(model => model.CostNanoUsd),
                        models);
                })
                .ToArray();

            return ValueTask.FromResult(new StatisticsSnapshot(days));
        }
    }

    private static void Merge<TKey>(
        Dictionary<TKey, MutableUsage> target,
        TKey key,
        UsageAggregate entry)
        where TKey : notnull
    {
        if (!target.TryGetValue(key, out MutableUsage? usage))
        {
            usage = new MutableUsage();
            target.Add(key, usage);
        }

        usage.RequestCount += entry.RequestCount;
        usage.InputTokens += entry.InputTokens;
        usage.CachedInputTokens += entry.CachedInputTokens;
        usage.OutputTokens += entry.OutputTokens;
        usage.CostNanoUsd += entry.CostNanoUsd;
        usage.DurationMilliseconds += entry.DurationMilliseconds;
        usage.UsageMissingCount += entry.UsageMissingCount;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        DateTimeOffset minimumHour = new(
            now.UtcDateTime.Date.AddHours(now.Hour).AddHours(-_hourlyRetentionHours + 1),
            TimeSpan.Zero);
        DateOnly minimumDay = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-_dailyRetentionDays + 1);

        foreach (HourlyKey key in _hourly.Keys.Where(key => key.PeriodHour < minimumHour).ToArray())
            _hourly.Remove(key);
        foreach (DailyKey key in _daily.Keys.Where(key => key.Date < minimumDay).ToArray())
            _daily.Remove(key);
    }

    private readonly record struct HourlyKey(
        DateTimeOffset PeriodHour,
        long UserId,
        long KeyId,
        string ModelAlias);

    private readonly record struct DailyKey(
        DateOnly Date,
        long UserId,
        long KeyId,
        string ModelAlias);

    private sealed class MutableUsage
    {
        public long RequestCount;
        public long InputTokens;
        public long CachedInputTokens;
        public long OutputTokens;
        public long CostNanoUsd;
        public long DurationMilliseconds;
        public long UsageMissingCount;
    }
}
