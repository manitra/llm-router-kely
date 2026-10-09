using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;
using RouterKely.Runtime;
using Xunit;

namespace RouterKely.Unit.Runtime;

// The configuration file uses literal secrets, so this class needs no process environment variables.
public sealed class RouterRuntimeTests : IDisposable
{
    private const string FastModel =
        """{ "alias": "fast", "upstreamModel": "deepseek-chat", "inputNanoUsdPerMillion": 1, "cachedInputNanoUsdPerMillion": 0, "outputNanoUsdPerMillion": 2 }""";

    private const string SmartModel =
        """{ "alias": "smart", "upstreamModel": "deepseek-reasoner", "inputNanoUsdPerMillion": 3, "cachedInputNanoUsdPerMillion": 0, "outputNanoUsdPerMillion": 4 }""";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-runtime-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task ReloadAppliesModelsUpstreamAndLimitsWithoutRestart()
    {
        WriteConfiguration(Config(FastModel));
        (RouterRuntime runtime, _, _) = Compose(IdentitySnapshot.Empty);
        Assert.Equal("fast", Assert.Single(runtime.Current.Routes).Alias);

        WriteConfiguration(Config(
            $"{FastModel}, {SmartModel}",
            upstreamBaseUrl: "https://api.example.com/v1",
            maxRequestBodyBytes: 2_048));
        IReadOnlyList<string> restartRequired = await runtime.ReloadAsync(CancellationToken.None);

        Assert.Empty(restartRequired);
        Assert.Equal(["fast", "smart"], runtime.Current.Routes.Select(route => route.Alias));
        Assert.Equal("deepseek-reasoner", runtime.Current.Routes[1].UpstreamModel);
        Assert.Equal("https://api.example.com/v1/", runtime.Current.UpstreamBaseUri.ToString());
        Assert.Equal(2_048, runtime.Current.MaxRequestBodyBytes);
    }

    [Fact]
    public async Task ReloadRotatesTheAdministratorKeyAndKeepsQuotaAndConcurrencyState()
    {
        WriteConfiguration(Config(FastModel));
        var fileIdentities = new IdentitySnapshot(
            1,
            [new IdentityUser(7, "User", "user@example.com", IdentityRole.User, true, 1_000)],
            [new IdentityKey(42, 7, "Key", new string('0', 64), "sk-rk_test", "test", true)]);
        (RouterRuntime runtime, ApiKeyAuthenticator authenticator, UsageAccumulator usage) =
            Compose(fileIdentities);

        Assert.True(authenticator.TryAuthenticateToken("sk-rk-admin-1".AsSpan(), out _));
        Assert.True(usage.TryGetAccount(42, out UsageAccount? account));
        account!.Record(
            new ModelRoute(0, "fast", "deepseek-chat"),
            UsageOutcome.Success,
            new UsageObservation(10, 0, 5, true),
            500,
            3);
        Assert.Equal(500, account.Quota.CurrentUsageNanoUsd);

        // A credential rotation plus a new route must both land, without losing the day's spend.
        WriteConfiguration(Config($"{FastModel}, {SmartModel}", clientApiKey: "sk-rk-admin-2"));
        IReadOnlyList<string> restartRequired = await runtime.ReloadAsync(CancellationToken.None);

        Assert.Empty(restartRequired);
        Assert.False(authenticator.TryAuthenticateToken("sk-rk-admin-1".AsSpan(), out _));
        Assert.True(authenticator.TryAuthenticateToken("sk-rk-admin-2".AsSpan(), out _));
        Assert.Equal(2, runtime.Current.Routes.Length);
        Assert.True(usage.TryGetAccount(42, out UsageAccount? rebuilt));
        Assert.Equal(500, rebuilt!.Quota.CurrentUsageNanoUsd);
        Assert.True(rebuilt.Concurrency.TryAcquire());
        rebuilt.Concurrency.Release();
    }

    [Fact]
    public async Task ReloadRejectsAnInvalidFileAndKeepsTheRunningConfiguration()
    {
        WriteConfiguration(Config(FastModel));
        (RouterRuntime runtime, _, _) = Compose(IdentitySnapshot.Empty);
        RouterSnapshot before = runtime.Current;

        WriteConfiguration(Config(FastModel, listenUrl: "not-a-url"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await runtime.ReloadAsync(CancellationToken.None));

        Assert.Same(before, runtime.Current);
    }

    [Fact]
    public async Task ReloadReportsOnlyTheSettingsThatStillNeedARestart()
    {
        WriteConfiguration(Config(FastModel));
        (RouterRuntime runtime, _, _) = Compose(IdentitySnapshot.Empty);

        WriteConfiguration(Config($"{FastModel}, {SmartModel}"));
        Assert.Empty(await runtime.ReloadAsync(CancellationToken.None));

        WriteConfiguration(Config(
            FastModel,
            listenUrl: "http://127.0.0.1:18099",
            hourlyRetentionHours: 48));
        IReadOnlyList<string> restartRequired = await runtime.ReloadAsync(CancellationToken.None);

        Assert.Contains("Listen URL", restartRequired);
        Assert.Contains("Statistics retention, flush interval and persistence directory", restartRequired);
        // The reported field is still applied to the file and visible, even though the listener keeps
        // the address the process was started with.
        Assert.Equal("http://127.0.0.1:18099", runtime.Current.Configuration.ListenUrl);
    }

    [Fact]
    public async Task ReloadReportsThePersistenceDirectoryAsRestartOnly()
    {
        WriteConfiguration(Config(FastModel));
        (RouterRuntime runtime, _, _) = Compose(IdentitySnapshot.Empty);

        WriteConfiguration(Config(FastModel, persistenceDirectory: "/data/usage"));
        IReadOnlyList<string> restartRequired = await runtime.ReloadAsync(CancellationToken.None);

        Assert.Contains("Statistics retention, flush interval and persistence directory", restartRequired);
        Assert.Equal("/data/usage", runtime.Current.Configuration.Statistics.PersistenceDirectoryPath);
    }

    [Fact]
    public void CreateRoutesCarriesTheVisionCapability()
    {
        WriteConfiguration(Config(
            """{ "alias": "fast", "upstreamModel": "deepseek-flash", "inputNanoUsdPerMillion": 1, "cachedInputNanoUsdPerMillion": 0, "outputNanoUsdPerMillion": 2, "supportsVision": true }"""));

        ModelRoute route = Assert.Single(RouterRuntime.CreateRoutes(LocalConfiguration.Load(_path).RouterKely));

        Assert.True(route.SupportsVision);
    }

    private (RouterRuntime Runtime, ApiKeyAuthenticator Authenticator, UsageAccumulator Usage) Compose(
        IdentitySnapshot fileIdentities)
    {
        RouterConfiguration configuration = LocalConfiguration.Load(_path).RouterKely;
        var authenticator = new ApiKeyAuthenticator(
            configuration.ClientApiKey,
            RouterRuntime.CreateEnvironmentAdministrator(configuration),
            configuration.Identity.EnvironmentAdminKeyId,
            "Environment administrator",
            fileIdentities);
        var usage = new UsageAccumulator(
            RouterRuntime.CreateRoutes(configuration),
            authenticator.Snapshot,
            configuration.MaxConcurrentRequestsPerUser ?? 32);
        var runtime = new RouterRuntime(
            _path,
            new StubIdentityProvider(fileIdentities),
            authenticator,
            usage,
            configuration);
        return (runtime, authenticator, usage);
    }

    private void WriteConfiguration(string json) => File.WriteAllText(_path, json);

    private static string Config(
        string models,
        string listenUrl = "http://127.0.0.1:18080",
        string clientApiKey = "sk-rk-admin-1",
        string upstreamBaseUrl = "https://api.deepseek.com/v1/",
        int maxRequestBodyBytes = 1_024,
        int hourlyRetentionHours = 72,
        string persistenceDirectory = "") => $$"""
        {
          "routerKely": {
            "listenUrl": "{{listenUrl}}",
            "clientApiKey": "{{clientApiKey}}",
            "identity": {
              "filePath": "router-kely.identities.json",
              "maxUsers": 256,
              "maxKeys": 1024,
              "environmentAdminUserId": 1,
              "environmentAdminKeyId": 1,
              "environmentAdminName": "Admin",
              "environmentAdminEmail": "admin@localhost"
            },
            "upstream": {
              "baseUrl": "{{upstreamBaseUrl}}",
              "apiKey": "sk-rk-upstream-1"
            },
            "models": [ {{models}} ],
            "dailyQuotaNanoUsd": null,
            "statistics": {
              "flushIntervalMilliseconds": 1000,
              "hourlyRetentionHours": {{hourlyRetentionHours}},
              "dailyRetentionDays": 7,
              "persistenceDirectoryPath": "{{persistenceDirectory}}"
            },
            "maxRequestBodyBytes": {{maxRequestBodyBytes}},
            "maxModelPrefixBytes": 128,
            "maxConcurrentRequests": 8,
            "maxConcurrentRequestsPerUser": 4
          }
        }
        """;

    private sealed class StubIdentityProvider(IdentitySnapshot snapshot) : IIdentityProvider
    {
        public ValueTask<IdentitySnapshot> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<IdentitySnapshot> ApplyAsync(
            IdentityMutation mutation,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
