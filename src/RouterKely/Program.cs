using System.Net;
using RouterKely.Compatibility;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;
using RouterKely.Identity;
using RouterKely.Proxy;
using RouterKely.Statistics;
using RouterKely.Ui;

string configPath = Environment.GetEnvironmentVariable("ROUTERKELY_CONFIG")
    ?? FindLocalConfiguration();
LocalConfiguration localConfiguration = LocalConfiguration.Load(configPath);
RouterConfiguration configuration = localConfiguration.RouterKely;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(configuration.ListenUrl);

var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,
    UseCookies = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    MaxConnectionsPerServer = 256,
    ConnectTimeout = TimeSpan.FromSeconds(5)
};

var client = new HttpClient(handler)
{
    Timeout = Timeout.InfiniteTimeSpan
};

ModelRoute[] routes = configuration.Models
    .Select((model, index) => new ModelRoute(
        index,
        model.Alias,
        model.UpstreamModel,
        model.InputNanoUsdPerMillion,
        model.CachedInputNanoUsdPerMillion,
        model.OutputNanoUsdPerMillion,
        model.MaxInputTokens,
        model.MaxOutputTokens,
        model.SupportsReasoning))
    .ToArray();

var identityProvider = new FileIdentityProvider(
    configuration.Identity.FilePath,
    configuration.Identity.MaxUsers,
    configuration.Identity.MaxKeys,
    configuration.Identity.EnvironmentAdminUserId,
    configuration.Identity.EnvironmentAdminKeyId);
IdentitySnapshot fileIdentities = await identityProvider.LoadAsync(CancellationToken.None);
var environmentAdmin = new IdentityUser(
    configuration.Identity.EnvironmentAdminUserId,
    configuration.Identity.EnvironmentAdminName,
    configuration.Identity.EnvironmentAdminEmail,
    IdentityRole.Admin,
    true,
    configuration.DailyQuotaNanoUsd,
    true);
var authenticator = new ApiKeyAuthenticator(
    configuration.ClientApiKey,
    environmentAdmin,
    configuration.Identity.EnvironmentAdminKeyId,
    "Environment administrator",
    fileIdentities);
var statistics = new InMemoryStatisticsProvider(
    configuration.Statistics.HourlyRetentionHours,
    configuration.Statistics.DailyRetentionDays);
var usage = new UsageAccumulator(routes, authenticator.Snapshot);
var proxy = new ProxyService(
    authenticator,
    client,
    new Uri(configuration.Upstream.BaseUrl.EndsWith('/')
        ? configuration.Upstream.BaseUrl
        : configuration.Upstream.BaseUrl + '/'),
    configuration.Upstream.ApiKey,
    routes,
    usage,
    configuration.MaxRequestBodyBytes,
    configuration.MaxModelPrefixBytes);
var compatibility = new CompatibilityService(
    authenticator,
    routes,
    usage,
    statistics);

builder.Services.AddSingleton<IHostedService>(_ => new StatisticsPump(
    usage,
    statistics,
    TimeSpan.FromMilliseconds(configuration.Statistics.FlushIntervalMilliseconds)));

WebApplication app = builder.Build();
var identityAdmin = new IdentityAdminService(
    identityProvider,
    fileIdentities,
    authenticator,
    usage,
    app.Logger);
var sessions = new UiSessionStore(authenticator);
var ui = new AdminUiService(authenticator, identityAdmin, sessions);

app.MapGet("/", static () => Results.Text("Router Kely is running. Use /v1 as the OpenAI-compatible base path.\n"));
app.MapGet("/health/live", static () => Results.Text("{\"status\":\"ok\"}", "application/json"));
app.MapGet("/health/ready", static () => Results.Text("{\"status\":\"ready\"}", "application/json"));
app.MapGet("/v1/models", proxy.WriteModelsAsync);
app.MapGet("/models", proxy.WriteModelsAsync);
app.MapGet("/v1/model/info", compatibility.WriteModelInfoAsync);
app.MapGet("/model/info", compatibility.WriteModelInfoAsync);
app.MapGet("/key/info", compatibility.WriteKeyInfoAsync);
app.MapGet("/user/daily/activity", compatibility.WriteDailyActivityAsync);
app.MapPost("/v1/chat/completions", proxy.ProxyChatCompletionsAsync);
app.MapPost("/chat/completions", proxy.ProxyChatCompletionsAsync);
app.MapGet("/ui", ui.RootAsync);
app.MapGet("/ui/login", ui.LoginPageAsync);
app.MapPost("/ui/login", ui.LoginAsync);
app.MapPost("/ui/logout", ui.LogoutAsync);
app.MapGet("/ui/admin/users", ui.UsersAsync);
app.MapGet("/ui/admin/users/{id:long}", ui.UserAsync);
app.MapPost("/ui/actions/users/create", ui.CreateUserAsync);
app.MapPost("/ui/actions/users/{id:long}/disable", ui.DisableUserAsync);
app.MapPost("/ui/actions/users/{id:long}/enable", ui.EnableUserAsync);
app.MapPost("/ui/actions/users/{id:long}/keys/create", ui.CreateKeyAsync);
app.MapPost("/ui/actions/keys/{id:long}/revoke", ui.RevokeKeyAsync);

await app.RunAsync();

static string FindLocalConfiguration()
{
    for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
         directory is not null;
         directory = directory.Parent)
    {
        string candidate = Path.Combine(directory.FullName, "config", "router-kely.local.json");
        if (File.Exists(candidate))
            return candidate;
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "config", "router-kely.local.json");
}
