namespace RouterKely.Core.Statistics;

public sealed class InMemoryStatisticsProvider : IStatisticsProvider
{
    public ValueTask<StatisticsSnapshot> RestoreAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public ValueTask WriteAsync(UsageBatch batch, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

