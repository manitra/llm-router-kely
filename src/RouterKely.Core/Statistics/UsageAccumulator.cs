using System.Collections.Frozen;
using RouterKely.Core.Concurrency;
using RouterKely.Core.Identity;
using RouterKely.Core.Routing;

namespace RouterKely.Core.Statistics;

public sealed class UsageAccumulator
{
    private static readonly int OutcomeCount = Enum.GetValues<UsageOutcome>().Length;
    private readonly object _gate = new();
    private readonly ModelRoute[] _routes;
    private readonly Dictionary<long, DailyQuotaCounter> _quotas = [];
    private readonly Dictionary<long, ConcurrencyLimiter> _concurrencyLimiters = [];
    private readonly Dictionary<long, UsageAccount> _allAccounts = [];
    private FrozenDictionary<long, UsageAccount> _activeAccounts = FrozenDictionary<long, UsageAccount>.Empty;
    private UsageAccount[] _accountSnapshot = [];

    public UsageAccumulator(
        ModelRoute[] routes,
        IdentitySnapshot identities,
        int maxConcurrentRequestsPerUser = 32)
    {
        _routes = routes;
        MaxConcurrentRequestsPerUser = maxConcurrentRequestsPerUser;
        UpdateIdentities(identities);
    }

    private int MaxConcurrentRequestsPerUser { get; }

    public bool TryGetAccount(long keyId, out UsageAccount? account) =>
        Volatile.Read(ref _activeAccounts).TryGetValue(keyId, out account);

    public void UpdateIdentities(IdentitySnapshot identities)
    {
        lock (_gate)
        {
            foreach (IdentityUser user in identities.Users)
            {
                if (!_concurrencyLimiters.ContainsKey(user.Id))
                    _concurrencyLimiters.Add(user.Id, new ConcurrencyLimiter(MaxConcurrentRequestsPerUser));

                if (!_quotas.TryGetValue(user.Id, out DailyQuotaCounter? quota))
                {
                    quota = new DailyQuotaCounter(user.QuotaNanoUsd);
                    _quotas.Add(user.Id, quota);
                }
                else
                {
                    quota.UpdateQuota(user.QuotaNanoUsd);
                }
            }

            var users = identities.Users.ToDictionary(user => user.Id);
            var active = new Dictionary<long, UsageAccount>();
            foreach (IdentityKey key in identities.Keys)
            {
                if (!users.TryGetValue(key.UserId, out IdentityUser? user))
                    continue;
                if (!_allAccounts.TryGetValue(key.Id, out UsageAccount? account))
                {
                    account = new UsageAccount(
                        user.Id,
                        key.Id,
                        _quotas[user.Id],
                        _concurrencyLimiters[user.Id],
                        _routes.Length,
                        OutcomeCount);
                    _allAccounts.Add(key.Id, account);
                }

                if (user.Enabled && key.Enabled)
                    active.Add(key.Id, account);
            }

            Volatile.Write(ref _accountSnapshot, _allAccounts.Values.ToArray());
            Volatile.Write(ref _activeAccounts, active.ToFrozenDictionary());
        }
    }

    public UsageBatch ExchangePending(DateTimeOffset now)
    {
        UsageAccount[] accounts = Volatile.Read(ref _accountSnapshot);
        var entries = new List<UsageAggregate>(accounts.Length * _routes.Length);
        DateTimeOffset hour = new(now.UtcDateTime.Date.AddHours(now.Hour), TimeSpan.Zero);

        foreach (UsageAccount account in accounts)
            account.Exchange(hour, _routes, OutcomeCount, entries);

        return new UsageBatch(entries.ToArray());
    }
}

public sealed class UsageAccount
{
    private readonly long _userId;
    private readonly long _keyId;
    private readonly int _outcomeCount;
    private readonly UsageCounters[] _counters;

    internal UsageAccount(
        long userId,
        long keyId,
        DailyQuotaCounter quota,
        ConcurrencyLimiter concurrency,
        int routeCount,
        int outcomeCount)
    {
        _userId = userId;
        _keyId = keyId;
        _outcomeCount = outcomeCount;
        Quota = quota;
        Concurrency = concurrency;
        _counters = Enumerable.Range(0, routeCount * outcomeCount)
            .Select(static _ => new UsageCounters())
            .ToArray();
    }

    public DailyQuotaCounter Quota { get; }

    public ConcurrencyLimiter Concurrency { get; }

    public void Record(
        ModelRoute route,
        UsageOutcome outcome,
        UsageObservation usage,
        long costNanoUsd,
        long durationMilliseconds)
    {
        Quota.Add(costNanoUsd);
        int index = checked((route.StatisticsIndex * _outcomeCount) + (int)outcome);
        _counters[index].Add(usage, costNanoUsd, durationMilliseconds);
    }

    internal void Exchange(
        DateTimeOffset hour,
        ModelRoute[] routes,
        int outcomeCount,
        List<UsageAggregate> entries)
    {
        for (int routeIndex = 0; routeIndex < routes.Length; routeIndex++)
        {
            for (int outcomeIndex = 0; outcomeIndex < outcomeCount; outcomeIndex++)
            {
                UsageCounters.Snapshot snapshot = _counters[(routeIndex * outcomeCount) + outcomeIndex].Exchange();
                if (snapshot.RequestCount == 0)
                    continue;

                entries.Add(new UsageAggregate(
                    hour,
                    _userId,
                    _keyId,
                    routes[routeIndex].Alias,
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
