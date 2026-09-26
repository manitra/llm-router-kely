using RouterKely.Core.Statistics;

namespace RouterKely.Statistics;

public sealed class StatisticsPump : BackgroundService
{
    private readonly UsageAccumulator _accumulator;
    private readonly IStatisticsProvider _provider;
    private readonly TimeSpan _interval;

    public StatisticsPump(
        UsageAccumulator accumulator,
        IStatisticsProvider provider,
        TimeSpan interval)
    {
        _accumulator = accumulator;
        _provider = provider;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        UsageBatch batch = _accumulator.ExchangePending(DateTimeOffset.UtcNow);
        return batch.Entries.Length == 0
            ? ValueTask.CompletedTask
            : _provider.WriteAsync(batch, cancellationToken);
    }
}
