namespace RouterKely.Core.Statistics;

public sealed class DailyQuotaCounter
{
    private readonly object _rolloverGate = new();
    private long _quotaNanoUsd;
    private long _dayNumber = UtcDayNumber();
    private long _usageNanoUsd;

    public DailyQuotaCounter(long? quotaNanoUsd)
    {
        if (quotaNanoUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(quotaNanoUsd));

        _quotaNanoUsd = quotaNanoUsd ?? -1;
    }

    public long? QuotaNanoUsd
    {
        get
        {
            long quota = Volatile.Read(ref _quotaNanoUsd);
            return quota < 0 ? null : quota;
        }
    }

    public long CurrentUsageNanoUsd
    {
        get
        {
            EnsureCurrentDay();
            return Interlocked.Read(ref _usageNanoUsd);
        }
    }

    public bool IsExceeded =>
        Volatile.Read(ref _quotaNanoUsd) is >= 0 and var quota && CurrentUsageNanoUsd >= quota;

    public void UpdateQuota(long? quotaNanoUsd)
    {
        if (quotaNanoUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(quotaNanoUsd));
        Volatile.Write(ref _quotaNanoUsd, quotaNanoUsd ?? -1);
    }

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
