using RouterKely.Configuration;
using Xunit;

namespace RouterKely.Unit.Configuration;

public sealed class ConfigurationAdminServiceTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-config-{Guid.NewGuid():N}.json");
    private const string AdminEnvVar = "ROUTERKELY_ADMIN_API_KEY";
    private const string UpstreamEnvVar = "ROUTERKELY_DEEPSEEK_API_KEY";

    public ConfigurationAdminServiceTests()
    {
        Environment.SetEnvironmentVariable(AdminEnvVar, "sk-rk-from-env");
        Environment.SetEnvironmentVariable(UpstreamEnvVar, "sk-deepseek-from-env");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }

    [Fact]
    public void SaveValidFormRoundTripsThroughDisk()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);

        var form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.ListenUrl = "http://127.0.0.1:9090";
        form.UpstreamBaseUrl = "https://api.deepseek.com/v1/";
        form.IdentityMaxUsers = "64";
        form.Models[0].Alias = "deepseek-fast-edited";

        service.Save(form);
        LocalConfiguration raw = service.LoadRaw();
        LocalConfiguration expanded = service.Load();

        Assert.Equal("http://127.0.0.1:9090", raw.RouterKely.ListenUrl);
        Assert.Equal(64, raw.RouterKely.Identity.MaxUsers);
        Assert.Equal("deepseek-fast-edited", raw.RouterKely.Models[0].Alias);
        // The on-disk file keeps the literal ${VAR} reference; the resolved value
        // only appears on the loaded/expanded object.
        Assert.Equal("${ROUTERKELY_ADMIN_API_KEY}", raw.RouterKely.ClientApiKey);
        Assert.Equal("${ROUTERKELY_DEEPSEEK_API_KEY}", raw.RouterKely.Upstream.ApiKey);
        Assert.Equal("sk-rk-from-env", expanded.RouterKely.ClientApiKey);
        Assert.Equal("sk-deepseek-from-env", expanded.RouterKely.Upstream.ApiKey);
    }

    [Fact]
    public void SaveWritesAtomicallyLeavingNoTempFileBehind()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        var form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.IdentityMaxUsers = "10";

        service.Save(form);

        string directory = Path.GetDirectoryName(_tempPath)!;
        Assert.Empty(Directory.EnumerateFiles(directory, ".router-kely*.tmp"));
    }

    [Fact]
    public void SaveRejectsInvalidValuesWithoutTouchingFile()
    {
        Seed();
        string original = File.ReadAllText(_tempPath);
        var service = new ConfigurationAdminService(_tempPath);
        var form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.IdentityMaxUsers = "0"; // below the configured minimum of 1

        var exception = Assert.Throws<InvalidOperationException>(() => service.Save(form));
        Assert.Contains("Max users", exception.Message);
        Assert.Equal(original, File.ReadAllText(_tempPath));
    }

    [Fact]
    public void SaveAcceptsBlankDailyQuotaAsUnlimited()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        var form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.DailyQuotaUsd = string.Empty;
        service.Save(form);

        Assert.Null(service.Load().RouterKely.DailyQuotaNanoUsd);
    }

    [Fact]
    public void ToFormConvertsNanoUsdBackToUsd()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());

        Assert.Equal("0", form.Models[0].InputUsdPerMillion);
    }

    [Fact]
    public void ToFormExposesLiteralEnvReferenceInsteadOfResolvedSecret()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());

        Assert.Equal("${ROUTERKELY_ADMIN_API_KEY}", form.ClientApiKey);
        Assert.Equal("${ROUTERKELY_DEEPSEEK_API_KEY}", form.UpstreamApiKey);
    }
    private void Seed()
    {
        string template = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "config", "router-kely.local.json.example");
        template = Path.GetFullPath(template);
        File.Copy(template, _tempPath);
    }
}
