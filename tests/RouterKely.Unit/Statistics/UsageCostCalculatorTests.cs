using RouterKely.Core.Statistics;
using Xunit;

namespace RouterKely.Unit.Statistics;

public sealed class UsageCostCalculatorTests
{
    [Fact]
    public void CalculateSeparatesCachedAndUncachedInput()
    {
        var usage = new UsageObservation(1_000, 400, 200, true);

        long cost = UsageCostCalculator.Calculate(
            usage,
            inputNanoUsdPerMillion: 1_000_000_000,
            cachedInputNanoUsdPerMillion: 100_000_000,
            outputNanoUsdPerMillion: 2_000_000_000);

        Assert.Equal(1_040_000, cost);
    }

    [Fact]
    public void CalculateRoundsEachComponentUpToOneNanoUsd()
    {
        var usage = new UsageObservation(1, 0, 1, true);

        long cost = UsageCostCalculator.Calculate(usage, 1, 0, 1);

        Assert.Equal(2, cost);
    }
}

