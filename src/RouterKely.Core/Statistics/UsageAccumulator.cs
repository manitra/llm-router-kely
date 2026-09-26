using RouterKely.Core.Routing;

namespace RouterKely.Core.Statistics;

public sealed class UsageAccumulator
{
    private static readonly int OutcomeCount = Enum.GetValues<UsageOutcome>().Length;

    private readonly long _userId;
    private readonly long _keyId;
    private readonly ModelRoute[] _routes;
    private readonly UsageCounters[] _counters;

    public UsageAccumulator(long userId, long keyId, ModelRoute[] routes, long? dailyQuotaNanoUsd)
    {
        _userId = userId;
        _keyId = keyId;
        _routes = routes;
        _counters = Enumerable.Range(0, routes.Length * OutcomeCount)
            .Select(static _ => new UsageCounters())
            .ToArray();
        Quota = new DailyQuotaCounter(dailyQuotaNanoUsd);
    }

    public DailyQuotaCounter Quota { get; }

    public void Record(
        ModelRoute route,
        UsageOutcome outcome,
        UsageObservation usage,
        long costNanoUsd,
        long durationMilliseconds)
    {
        Quota.Add(costNanoUsd);
        int index = checked((route.StatisticsIndex * OutcomeCount) + (int)outcome);
        _counters[index].Add(usage, costNanoUsd, durationMilliseconds);
    }

    public UsageBatch ExchangePending(DateTimeOffset now)
    {
        var entries = new List<UsageAggregate>(_counters.Length);
        DateTimeOffset hour = new(now.UtcDateTime.Date.AddHours(now.Hour), TimeSpan.Zero);

        for (int routeIndex = 0; routeIndex < _routes.Length; routeIndex++)
        {
            for (int outcomeIndex = 0; outcomeIndex < OutcomeCount; outcomeIndex++)
            {
                UsageCounters.Snapshot snapshot = _counters[(routeIndex * OutcomeCount) + outcomeIndex].Exchange();
                if (snapshot.RequestCount == 0)
                    continue;

                entries.Add(new UsageAggregate(
                    hour,
                    _userId,
                    _keyId,
                    _routes[routeIndex].Alias,
                    (UsageOutcome)outcomeIndex,
                    snapshot.RequestCount,
                    snapshot.InputTokens,
                    snapshot.CachedInputTokens,
                    snapshot.OutputTokens,
                    snapshot.CostNanoUsd,
                    snapshot.DurationMilliseconds,
                    snapshot.UsageMissingCount));
            }
        }

        return new UsageBatch(entries.ToArray());
    }

    private sealed class UsageCounters
    {
        private long _requestCount;
        private long _inputTokens;
        private long _cachedInputTokens;
        private long _outputTokens;
        private long _costNanoUsd;
        private long _durationMilliseconds;
        private long _usageMissingCount;

        public void Add(UsageObservation usage, long costNanoUsd, long durationMilliseconds)
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _inputTokens, usage.InputTokens);
            Interlocked.Add(ref _cachedInputTokens, usage.CachedInputTokens);
            Interlocked.Add(ref _outputTokens, usage.OutputTokens);
            Interlocked.Add(ref _costNanoUsd, costNanoUsd);
            Interlocked.Add(ref _durationMilliseconds, durationMilliseconds);
            if (!usage.Found)
                Interlocked.Increment(ref _usageMissingCount);
        }

        public Snapshot Exchange() => new(
            Interlocked.Exchange(ref _requestCount, 0),
            Interlocked.Exchange(ref _inputTokens, 0),
            Interlocked.Exchange(ref _cachedInputTokens, 0),
            Interlocked.Exchange(ref _outputTokens, 0),
            Interlocked.Exchange(ref _costNanoUsd, 0),
            Interlocked.Exchange(ref _durationMilliseconds, 0),
            Interlocked.Exchange(ref _usageMissingCount, 0));

        public readonly record struct Snapshot(
            long RequestCount,
            long InputTokens,
            long CachedInputTokens,
            long OutputTokens,
            long CostNanoUsd,
            long DurationMilliseconds,
            long UsageMissingCount);
    }
}
