namespace RouterKely.Core.Statistics;

public interface IStatisticsProvider
{
    ValueTask<StatisticsSnapshot> RestoreAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(UsageBatch batch, CancellationToken cancellationToken);

    ValueTask<StatisticsSnapshot> QueryAsync(
        DateOnly startDate,
        DateOnly endDate,
        long? userId,
        long? keyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the retained daily cells with their user and key intact, for the administration UI
    /// and for durable stores that must not lose attribution. Cold path only.
    /// </summary>
    ValueTask<UsageRowsSnapshot> QueryUsageRowsAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken);
}
