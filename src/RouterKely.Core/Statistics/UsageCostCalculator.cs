namespace RouterKely.Core.Statistics;

public static class UsageCostCalculator
{
    public static long Calculate(
        UsageObservation usage,
        long inputNanoUsdPerMillion,
        long cachedInputNanoUsdPerMillion,
        long outputNanoUsdPerMillion)
    {
        if (!usage.Found)
            return 0;

        long uncachedInput = Math.Max(0, usage.InputTokens - usage.CachedInputTokens);
        return checked(
            Cost(uncachedInput, inputNanoUsdPerMillion) +
            Cost(usage.CachedInputTokens, cachedInputNanoUsdPerMillion) +
            Cost(usage.OutputTokens, outputNanoUsdPerMillion));
    }

    private static long Cost(long tokens, long pricePerMillion)
    {
        if (tokens == 0 || pricePerMillion == 0)
            return 0;

        Int128 numerator = checked((Int128)tokens * pricePerMillion);
        return checked((long)((numerator + 999_999) / 1_000_000));
    }
}

