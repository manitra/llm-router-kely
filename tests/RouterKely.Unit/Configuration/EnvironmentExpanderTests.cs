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
        Assert.Equal("hello world", Expand("hello world"));
        Assert.Equal("", Expand(""));
    }

    [Fact]
    public void ExpandsReferenceToEnvVarValue()
    {
        Environment.SetEnvironmentVariable(Var, "secret-value");
        Assert.Equal("prefix secret-value suffix", Expand($"prefix ${{{Var}}} suffix"));
    }

    [Fact]
    public void ExpandsMultipleReferencesInSameString()
    {
        Environment.SetEnvironmentVariable(Var, "value");
        Assert.Equal("value/value", Expand($"${{{Var}}}/${{{Var}}}"));
    }

    [Fact]
    public void RecordsMissingVariableWithoutThrowing()
    {
        var missing = new List<MissingEnvironmentVariable>();

        string expanded = EnvironmentExpander.Expand($"${{{Var}}}", "RouterKely.Upstream.ApiKey", missing);

        // The literal reference survives so the caller can still report it; nothing throws here.
        Assert.Equal($"${{{Var}}}", expanded);
        MissingEnvironmentVariable entry = Assert.Single(missing);
        Assert.Equal(Var, entry.Name);
        Assert.Equal("RouterKely.Upstream.ApiKey", entry.FieldPath);
    }

    [Fact]
    public void RecordsEmptyEnvVarAsMissing()
    {
        Environment.SetEnvironmentVariable(Var, "");
        var missing = new List<MissingEnvironmentVariable>();

        EnvironmentExpander.Expand($"${{{Var}}}", "f", missing);

        Assert.Single(missing);
    }

    [Fact]
    public void RecordsEveryMissingReferenceSoOneStartupCanReportThemAll()
    {
        const string other = "ROUTERKELY_TEST_OTHER";
        Environment.SetEnvironmentVariable(Var, null);
        Environment.SetEnvironmentVariable(other, null);
        var missing = new List<MissingEnvironmentVariable>();

        try
        {
            EnvironmentExpander.Expand($"${{{Var}}}/${{{other}}}/${{{Var}}}", "f", missing);

            Assert.Equal(new[] { Var, other, Var }, missing.Select(item => item.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    [Fact]
    public void EscapedDollarYieldsLiteralDollarAndIsNotReported()
    {
        Environment.SetEnvironmentVariable(Var, "ignored");
        var missing = new List<MissingEnvironmentVariable>();

        string expanded = EnvironmentExpander.Expand($"$$$$ literal $${{{Var}}}", "f", missing);

        Assert.Equal($"$$ literal ${{{Var}}}", expanded);
        Assert.Empty(missing);
    }

    private static string Expand(string value) =>
        EnvironmentExpander.Expand(value, "f", new List<MissingEnvironmentVariable>());
}
