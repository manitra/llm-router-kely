using RouterKely.Core.Identity;
using Xunit;

namespace RouterKely.Unit;

public sealed class ScaffoldingTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.NotNull(typeof(IIdentityProvider).Assembly);
}
