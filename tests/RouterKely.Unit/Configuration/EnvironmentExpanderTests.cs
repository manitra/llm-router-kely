using RouterKely.Configuration;
using Xunit;

namespace RouterKely.Unit.Configuration;

public sealed class EnvironmentExpanderTests : IDisposable
{
    private const string Var = "ROUTERKELY_TEST_VAR";

    public EnvironmentExpanderTests()
    {
        Environment.SetEnvironmentVariable(Var, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Var, null);
    }

    [Fact]
    public void LeavesPlainStringUntouched()
    {
        Assert.Equal("hello world", EnvironmentExpander.Expand("hello world", "f"));
        Assert.Equal("", EnvironmentExpander.Expand("", "f"));
    }

    [Fact]
    public void ExpandsReferenceToEnvVarValue()
    {
        Environment.SetEnvironmentVariable(Var, "secret-value");
        Assert.Equal("prefix secret-value suffix",
            EnvironmentExpander.Expand("prefix ${ROUTERKELY_TEST_VAR} suffix", "f"));
    }

    [Fact]
    public void ExpandsMultipleReferencesInSameString()
    {
        Environment.SetEnvironmentVariable(Var, "value");
        Assert.Equal("value/value",
            EnvironmentExpander.Expand("${ROUTERKELY_TEST_VAR}/${ROUTERKELY_TEST_VAR}", "f"));
    }

    [Fact]
    public void ThrowsWithFieldPathWhenEnvVarMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EnvironmentExpander.Expand("${ROUTERKELY_TEST_VAR}", "RouterKely.Upstream.ApiKey"));
        Assert.Contains("RouterKely.Upstream.ApiKey", exception.Message);
        Assert.Contains("ROUTERKELY_TEST_VAR", exception.Message);
    }

    [Fact]
    public void ThrowsWhenEnvVarIsEmptyString()
    {
        Environment.SetEnvironmentVariable(Var, "");
        Assert.Throws<InvalidOperationException>(
            () => EnvironmentExpander.Expand("${ROUTERKELY_TEST_VAR}", "f"));
    }

    [Fact]
    public void EscapedDollarYieldsLiteralDollar()
    {
        Environment.SetEnvironmentVariable(Var, "ignored");
        Assert.Equal("$$ literal ${ROUTERKELY_TEST_VAR}",
            EnvironmentExpander.Expand("$$$$ literal $${ROUTERKELY_TEST_VAR}", "f"));
    }
}
