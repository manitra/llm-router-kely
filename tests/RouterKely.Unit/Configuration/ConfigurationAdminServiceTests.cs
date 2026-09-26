using RouterKely.Configuration;
using Xunit;

namespace RouterKely.Unit.Configuration;

public sealed class ConfigurationAdminServiceTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-config-{Guid.NewGuid():N}.json");

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

        var form = ConfigurationAdminService.ToForm(service.Load());
        form.ListenUrl = "http://127.0.0.1:9090";
        form.UpstreamBaseUrl = "https://api.deepseek.com/v1/";
        form.IdentityMaxUsers = "64";
        form.Models[0].Alias = "deepseek-fast-edited";

        service.Save(form);
        LocalConfiguration reloaded = service.Load();

        Assert.Equal("http://127.0.0.1:9090", reloaded.RouterKely.ListenUrl);
        Assert.Equal(64, reloaded.RouterKely.Identity.MaxUsers);
        Assert.Equal("deepseek-fast-edited", reloaded.RouterKely.Models[0].Alias);
        // Secrets are preserved untouched when round-tripping through the editor.
        Assert.Equal("sk-rk-local-change-me", reloaded.RouterKely.ClientApiKey);
        Assert.Equal("replace-with-your-deepseek-api-key", reloaded.RouterKely.Upstream.ApiKey);
    }

    [Fact]
    public void SaveWritesAtomicallyLeavingNoTempFileBehind()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        var form = ConfigurationAdminService.ToForm(service.Load());
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
        var form = ConfigurationAdminService.ToForm(service.Load());
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
        var form = ConfigurationAdminService.ToForm(service.Load());
        form.DailyQuotaUsd = string.Empty;
        service.Save(form);

        Assert.Null(service.Load().RouterKely.DailyQuotaNanoUsd);
    }

    [Fact]
    public void ToFormConvertsNanoUsdBackToUsd()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.Load());

        Assert.Equal("0", form.Models[0].InputUsdPerMillion);
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
