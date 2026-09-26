namespace RouterKely.Core.Statistics;

public sealed class DailyQuotaCounter
{
    private readonly object _rolloverGate = new();
    private readonly long? _quotaNanoUsd;
    private long _dayNumber = UtcDayNumber();
    private long _usageNanoUsd;

    public DailyQuotaCounter(long? quotaNanoUsd)
    {
        if (quotaNanoUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(quotaNanoUsd));

        _quotaNanoUsd = quotaNanoUsd;
    }

    public long? QuotaNanoUsd => _quotaNanoUsd;

    public long CurrentUsageNanoUsd
    {
        get
        {
            EnsureCurrentDay();
            return Interlocked.Read(ref _usageNanoUsd);
        }
    }

    public bool IsExceeded =>
        _quotaNanoUsd is long quota && CurrentUsageNanoUsd >= quota;

    public void Add(long costNanoUsd)
    {
        EnsureCurrentDay();
        Interlocked.Add(ref _usageNanoUsd, costNanoUsd);
    }

    private void EnsureCurrentDay()
    {
        long today = UtcDayNumber();
        if (Volatile.Read(ref _dayNumber) == today)
            return;

        lock (_rolloverGate)
        {
            if (_dayNumber == today)
                return;

            Interlocked.Exchange(ref _usageNanoUsd, 0);
            Volatile.Write(ref _dayNumber, today);
        }
    }

    private static long UtcDayNumber() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay;
}

