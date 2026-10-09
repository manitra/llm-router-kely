using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterKely.Compatibility;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Statistics;
using RouterKely.Runtime;
using Xunit;

namespace RouterKely.Unit.Compatibility;

// The configuration file uses literal secrets, so this class needs no process environment variables.
public sealed class CompatibilityServiceTests : IDisposable
{
    private const string AdminKey = "sk-rk-admin-1";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-compat-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task ModelInfoAdvertisesTheVisionCapabilityOfEachAlias()
    {
        File.WriteAllText(_path, Config);
        CompatibilityService service = Compose();
        DefaultHttpContext context = CreateRequest();

        await service.WriteModelInfoAsync(context);
        await context.Response.CompleteAsync();

        using JsonDocument document = JsonDocument.Parse(ReadResponseBody(context));
        Dictionary<string, JsonElement> models = document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("model_name").GetString()!);

        Assert.True(models["fast"].GetProperty("model_info").GetProperty("supports_vision").GetBoolean());
        Assert.True(models["fast"].GetProperty("model_info").GetProperty("supports_image_input").GetBoolean());
        Assert.False(models["smart"].GetProperty("model_info").GetProperty("supports_vision").GetBoolean());
        Assert.False(models["smart"].GetProperty("model_info").GetProperty("supports_image_input").GetBoolean());
    }

    private CompatibilityService Compose()
    {
        RouterConfiguration configuration = LocalConfiguration.Load(_path).RouterKely;
        var authenticator = new ApiKeyAuthenticator(
            configuration.ClientApiKey,
            RouterRuntime.CreateEnvironmentAdministrator(configuration),
            configuration.Identity.EnvironmentAdminKeyId,
            "Environment administrator",
            IdentitySnapshot.Empty);
        var usage = new UsageAccumulator(
            RouterRuntime.CreateRoutes(configuration),
            authenticator.Snapshot,
            configuration.MaxConcurrentRequestsPerUser ?? 32);
        var runtime = new RouterRuntime(
            _path,
            new StubIdentityProvider(),
            authenticator,
            usage,
            configuration);
        return new CompatibilityService(authenticator, runtime, usage, new InMemoryStatisticsProvider());
    }

    private static DefaultHttpContext CreateRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/model/info";
        context.Request.Headers.Authorization = $"Bearer {AdminKey}";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private const string Config = $$"""
        {
          "routerKely": {
            "listenUrl": "http://127.0.0.1:18080",
            "clientApiKey": "{{AdminKey}}",
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
              "baseUrl": "https://upstream.example/v1/",
              "apiKey": "sk-rk-upstream-1"
            },
            "models": [
              {
                "alias": "fast",
                "upstreamModel": "deepseek-flash",
                "inputNanoUsdPerMillion": 1,
                "cachedInputNanoUsdPerMillion": 0,
                "outputNanoUsdPerMillion": 2,
                "supportsVision": true
              },
              {
                "alias": "smart",
                "upstreamModel": "deepseek-v4-pro",
                "inputNanoUsdPerMillion": 3,
                "cachedInputNanoUsdPerMillion": 0,
                "outputNanoUsdPerMillion": 4
              }
            ],
            "dailyQuotaNanoUsd": null,
            "statistics": {
              "flushIntervalMilliseconds": 1000,
              "hourlyRetentionHours": 72,
              "dailyRetentionDays": 7
            },
            "maxRequestBodyBytes": 8192,
            "maxModelPrefixBytes": 256,
            "maxConcurrentRequests": 8,
            "maxConcurrentRequestsPerUser": 4
          }
        }
        """;

    private sealed class StubIdentityProvider : IIdentityProvider
    {
        public ValueTask<IdentitySnapshot> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(IdentitySnapshot.Empty);

        public ValueTask<IdentitySnapshot> ApplyAsync(
            IdentityMutation mutation,
            long expectedVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
