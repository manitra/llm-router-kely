namespace RouterKely.Core.Statistics;

public interface IStatisticsProvider
{
    ValueTask<StatisticsSnapshot> RestoreAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(UsageBatch batch, CancellationToken cancellationToken);
}

