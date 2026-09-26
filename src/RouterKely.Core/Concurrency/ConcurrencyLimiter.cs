namespace RouterKely.Core.Concurrency;

public sealed class ConcurrencyLimiter
{
    private readonly int _limit;
    private int _active;

    public ConcurrencyLimiter(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public bool TryAcquire()
    {
        int active = Volatile.Read(ref _active);
        while (active < _limit)
        {
            int observed = Interlocked.CompareExchange(ref _active, active + 1, active);
            if (observed == active)
                return true;
            active = observed;
        }

        return false;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _active) < 0)
        {
            Interlocked.Increment(ref _active);
            throw new InvalidOperationException("Cannot release an unoccupied concurrency slot.");
        }
    }
}
