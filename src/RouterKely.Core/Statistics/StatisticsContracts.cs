namespace RouterKely.Core.Statistics;

public enum UsageOutcome
{
    Success,
    ClientError,
    UpstreamError,
    Cancelled
}

public readonly record struct UsageObservation(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    bool Found);

public readonly record struct UsageAggregate(
    DateTimeOffset PeriodHour,
    long UserId,
    long KeyId,
    string ModelAlias,
    UsageOutcome Outcome,
    long RequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CostNanoUsd,
    long DurationMilliseconds,
    long UsageMissingCount);

public sealed record UsageBatch(UsageAggregate[] Entries);

public readonly record struct ModelDailyUsage(
    string ModelAlias,
    long RequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CostNanoUsd);

public sealed record DailyUsage(
    DateOnly Date,
    long RequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CostNanoUsd,
    ModelDailyUsage[] Models);

public sealed record StatisticsSnapshot(DailyUsage[] Days);
