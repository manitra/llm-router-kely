using RouterKely.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace RouterKely.Unit.Configuration;

// Shares a collection with LocalConfigurationTests: both touch environment variables,
// and xUnit runs test classes in parallel within one process.
[Collection("configuration-env")]
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
    public void SaveRoundTripsDailyQuotaUsd()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.DailyQuotaUsd = "10";

        service.Save(form);

        Assert.Equal(10_000_000_000, service.LoadRaw().RouterKely.DailyQuotaNanoUsd);
        Assert.Equal("10", ConfigurationAdminService.ToForm(service.LoadRaw()).DailyQuotaUsd);
    }

    [Fact]
    public void SaveRoundTripsFractionalDailyQuota()
    {
        // A nanoUSD value below 1 USD renders as a fraction; the editor used to reject it.
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.DailyQuotaUsd = "0.00000001";

        service.Save(form);

        Assert.Equal(10, service.LoadRaw().RouterKely.DailyQuotaNanoUsd);
        Assert.Equal("0.00000001", ConfigurationAdminService.ToForm(service.LoadRaw()).DailyQuotaUsd);
    }

    [Fact]
    public void ToFormKeepsModelPriceInNanoUsdPerMillion()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());

        Assert.Equal("300000000", form.Models[0].InputUsdPerMillion);
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

    [Fact]
    public void FromFormKeepsModelRowsAlignedWhenAReasoningCheckboxIsUnchecked()
    {
        // Browsers omit unchecked checkboxes, so a positional parser would shift every later row.
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["models[0].alias"] = "fast",
            ["models[0].upstreamModel"] = "deepseek-chat",
            ["models[0].input"] = "1",
            ["models[0].cachedInput"] = "0",
            ["models[0].output"] = "2",
            ["models[1].alias"] = "pro",
            ["models[1].upstreamModel"] = "deepseek-reasoner",
            ["models[1].input"] = "3",
            ["models[1].cachedInput"] = "0",
            ["models[1].output"] = "4",
            ["models[1].supportsReasoning"] = "true",
            ["models[2].alias"] = "mini",
            ["models[2].upstreamModel"] = "deepseek-chat",
            ["models[2].input"] = "5",
            ["models[2].cachedInput"] = "0",
            ["models[2].output"] = "6",
        });

        ConfigForm parsed = ConfigurationAdminService.FromForm(form);

        Assert.Equal(3, parsed.Models.Length);
        Assert.Equal("fast", parsed.Models[0].Alias);
        Assert.False(parsed.Models[0].SupportsReasoning);
        Assert.Equal("pro", parsed.Models[1].Alias);
        Assert.True(parsed.Models[1].SupportsReasoning);
        Assert.Equal("mini", parsed.Models[2].Alias);
        Assert.False(parsed.Models[2].SupportsReasoning);
    }

    [Fact]
    public void TryAddAndRemoveModelKeepAtLeastOneRowAndRespectTheCap()
    {
        var form = new ConfigForm { Models = [new ConfigModelForm { Alias = "a", UpstreamModel = "b" }] };

        Assert.False(ConfigurationAdminService.TryRemoveModel(form, 0));
        Assert.Single(form.Models);

        Assert.True(ConfigurationAdminService.TryAddModel(form));
        form.Models[1].Alias = "c";
        Assert.True(ConfigurationAdminService.TryRemoveModel(form, 0));
        Assert.Equal("c", Assert.Single(form.Models).Alias);

        Assert.False(ConfigurationAdminService.TryRemoveModel(form, 99));
        Assert.True(ConfigurationAdminService.TryAddModel(form));
        form.Models[1].Alias = "d";

        while (ConfigurationAdminService.TryAddModel(form))
        {
        }
        Assert.Equal(RouterConfiguration.MaxModelRoutes, form.Models.Length);
        Assert.False(ConfigurationAdminService.TryAddModel(form));
    }

    [Fact]
    public void SavePersistsAVariableLengthModelList()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());
        Assert.True(ConfigurationAdminService.TryAddModel(form));

        ConfigModelForm added = form.Models[2];
        added.Alias = "deepseek-mini";
        added.UpstreamModel = "deepseek-chat";
        added.InputUsdPerMillion = "10";
        added.CachedInputUsdPerMillion = "0";
        added.OutputUsdPerMillion = "20";

        service.Save(form);
        ModelConfiguration[] saved = service.LoadRaw().RouterKely.Models;

        Assert.Equal(3, saved.Length);
        Assert.Equal("deepseek-mini", saved[2].Alias);
        Assert.Equal(10, saved[2].InputNanoUsdPerMillion);
        Assert.Equal(20, saved[2].OutputNanoUsdPerMillion);
    }

    [Fact]
    public void SaveRejectsMoreModelRoutesThanTheCap()
    {
        Seed();
        var service = new ConfigurationAdminService(_tempPath);
        ConfigForm form = ConfigurationAdminService.ToForm(service.LoadRaw());
        form.Models = Enumerable.Range(0, RouterConfiguration.MaxModelRoutes + 1)
            .Select(index => new ConfigModelForm
            {
                Alias = $"alias-{index}",
                UpstreamModel = "deepseek-chat",
                InputUsdPerMillion = "0",
                CachedInputUsdPerMillion = "0",
                OutputUsdPerMillion = "0",
            })
            .ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Save(form));

        Assert.Contains("model routes", exception.Message, StringComparison.OrdinalIgnoreCase);
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
