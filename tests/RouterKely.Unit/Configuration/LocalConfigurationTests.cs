using RouterKely.Configuration;
using Xunit;

namespace RouterKely.Unit.Configuration;

// Shares a collection with ConfigurationAdminServiceTests: both touch environment variables,
// and xUnit runs test classes in parallel within one process.
[Collection("configuration-env")]
public sealed class LocalConfigurationTests : IDisposable
{
    // Unique names keep these tests isolated from other classes that set the
    // production ROUTERKELY_* variables in the same process.
    private const string AdminEnvVar = "ROUTERKELY_TEST_LOCAL_ADMIN_KEY";
    private const string UpstreamEnvVar = "ROUTERKELY_TEST_LOCAL_UPSTREAM_KEY";

    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-config-{Guid.NewGuid():N}.json");

    public LocalConfigurationTests()
    {
        Environment.SetEnvironmentVariable(AdminEnvVar, "admin-secret");
        Environment.SetEnvironmentVariable(UpstreamEnvVar, "upstream-secret");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdminEnvVar, null);
        Environment.SetEnvironmentVariable(UpstreamEnvVar, null);
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void LoadCreatesTheConfigurationFileFromTheBuiltInDefaultWhenAbsent()
    {
        Assert.False(File.Exists(_tempPath));

        // The generated default references the production variable names, so provide them
        // and confirm the created file is immediately loadable.
        string? previousAdmin = Environment.GetEnvironmentVariable("ROUTERKELY_ADMIN_API_KEY");
        string? previousUpstream = Environment.GetEnvironmentVariable("ROUTERKELY_DEEPSEEK_API_KEY");
        Environment.SetEnvironmentVariable("ROUTERKELY_ADMIN_API_KEY", "admin-secret");
        Environment.SetEnvironmentVariable("ROUTERKELY_DEEPSEEK_API_KEY", "upstream-secret");
        try
        {
            LocalConfiguration loaded = LocalConfiguration.Load(_tempPath);

            Assert.True(File.Exists(_tempPath));
            string written = File.ReadAllText(_tempPath);
            Assert.Contains("${ROUTERKELY_ADMIN_API_KEY}", written, StringComparison.Ordinal);
            Assert.Contains("${ROUTERKELY_DEEPSEEK_API_KEY}", written, StringComparison.Ordinal);
            Assert.NotEmpty(loaded.RouterKely.Models);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROUTERKELY_ADMIN_API_KEY", previousAdmin);
            Environment.SetEnvironmentVariable("ROUTERKELY_DEEPSEEK_API_KEY", previousUpstream);
        }
    }

    [Fact]
    public void LoadDoesNotOverwriteAnExistingConfigurationFile()
    {
        Seed();
        string original = File.ReadAllText(_tempPath);

        LocalConfiguration.Load(_tempPath);

        Assert.Equal(original, File.ReadAllText(_tempPath));
    }

    [Fact]
    public void LoadReportsEveryMissingVariableInOneError()
    {
        Seed();
        Environment.SetEnvironmentVariable(AdminEnvVar, null);
        Environment.SetEnvironmentVariable(UpstreamEnvVar, null);

        var exception = Assert.Throws<InvalidOperationException>(() => LocalConfiguration.Load(_tempPath));

        // Both variables must be named in a single failure so the operator fixes them in one pass.
        Assert.Contains(AdminEnvVar, exception.Message);
        Assert.Contains(UpstreamEnvVar, exception.Message);
        Assert.Contains("RouterKely.ClientApiKey", exception.Message);
        Assert.Contains("RouterKely.Upstream.ApiKey", exception.Message);
        Assert.Contains(_tempPath, exception.Message);
    }

    [Fact]
    public void LoadReportsOnlyTheVariablesThatAreActuallyMissing()
    {
        Seed();
        Environment.SetEnvironmentVariable(UpstreamEnvVar, null);

        var exception = Assert.Throws<InvalidOperationException>(() => LocalConfiguration.Load(_tempPath));

        Assert.Contains(UpstreamEnvVar, exception.Message);
        Assert.DoesNotContain(AdminEnvVar, exception.Message);
    }

    [Fact]
    public void LoadExpandsEnvReferencesAndValidatePasses()
    {
        Seed();
        LocalConfiguration loaded = LocalConfiguration.Load(_tempPath);

        Assert.Equal("admin-secret", loaded.RouterKely.ClientApiKey);
        Assert.Equal("upstream-secret", loaded.RouterKely.Upstream.ApiKey);
    }

    [Fact]
    public void LoadRawKeepsLiteralEnvReferences()
    {
        Seed();
        LocalConfiguration raw = LocalConfiguration.LoadRaw(_tempPath);

        Assert.Equal($"${{{AdminEnvVar}}}", raw.RouterKely.ClientApiKey);
        Assert.Equal($"${{{UpstreamEnvVar}}}", raw.RouterKely.Upstream.ApiKey);
    }

    [Fact]
    public void LoadFailsWithDescriptiveErrorWhenEnvVarMissing()
    {
        Seed();
        Environment.SetEnvironmentVariable(UpstreamEnvVar, null);

        var exception = Assert.Throws<InvalidOperationException>(() => LocalConfiguration.Load(_tempPath));
        Assert.Contains("RouterKely.Upstream.ApiKey", exception.Message);
        Assert.Contains(UpstreamEnvVar, exception.Message);
    }

    private void Seed()
    {
        // Source-generated deserialization does not preserve property initializer
        // defaults, so a config that omits a field deserializes to 0/null. Keep this
        // fixture complete, matching the shipped example, so it exercises the
        // documented configuration instead of that gap.
        const string template = """
            {
              "routerKely": {
                "listenUrl": "http://127.0.0.1:8080",
                "clientApiKey": "${ADMIN_VAR}",
                "identity": {
                  "filePath": "router-kely.identities.json",
                  "maxUsers": 256,
                  "maxKeys": 1024,
                  "environmentAdminUserId": 1,
                  "environmentAdminKeyId": 1,
                  "environmentAdminName": "Local Administrator",
                  "environmentAdminEmail": "admin@localhost"
                },
                "upstream": {
                  "baseUrl": "https://api.deepseek.com/v1/",
                  "apiKey": "${UPSTREAM_VAR}"
                },
                "models": [
                  {
                    "alias": "test",
                    "upstreamModel": "deepseek-chat",
                    "inputNanoUsdPerMillion": 0,
                    "cachedInputNanoUsdPerMillion": 0,
                    "outputNanoUsdPerMillion": 0
                  }
                ],
                "statistics": {
                  "flushIntervalMilliseconds": 1000,
                  "hourlyRetentionHours": 72,
                  "dailyRetentionDays": 7
                },
                "maxRequestBodyBytes": 33554432,
                "maxModelPrefixBytes": 65536,
                "maxConcurrentRequests": 256,
                "maxConcurrentRequestsPerUser": 32
              }
            }
            """;
        File.WriteAllText(_tempPath, template
            .Replace("ADMIN_VAR", AdminEnvVar, StringComparison.Ordinal)
            .Replace("UPSTREAM_VAR", UpstreamEnvVar, StringComparison.Ordinal));
    }
}
