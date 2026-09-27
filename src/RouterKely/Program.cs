using System.Globalization;
using System.Net;
using RouterKely.Compatibility;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Statistics;
using RouterKely.Identity;
using RouterKely.Proxy;
using RouterKely.Runtime;
using RouterKely.Statistics;
using RouterKely.Ui;

// An empty value counts as unset: a platform that materializes every declared variable
// (Coolify does) can hand over an empty string for a row the operator left alone, and an
// empty path would otherwise resolve to the working directory instead of the volume.
string? configuredPath = Environment.GetEnvironmentVariable("ROUTERKELY_CONFIG");
string configPath = string.IsNullOrWhiteSpace(configuredPath)
    ? FindLocalConfiguration()
    : configuredPath;
LocalConfiguration localConfiguration;
try
{
    localConfiguration = LocalConfiguration.Load(configPath);
}
catch (InvalidOperationException exception)
{
    // Startup failures are read by an operator in the platform's log view, so report the
    // message without a stack trace.
    Console.Error.WriteLine($"router-kely: {exception.Message}");
    return 1;
}
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
    MaxConnectionsPerServer = configuration.EffectiveMaxConcurrentRequests,
    ConnectTimeout = TimeSpan.FromSeconds(5)
};

var client = new HttpClient(handler)
{
    Timeout = Timeout.InfiniteTimeSpan
};

var identityProvider = new FileIdentityProvider(
    configuration.Identity.FilePath,
    configuration.Identity.MaxUsers,
    configuration.Identity.MaxKeys,
    configuration.Identity.EnvironmentAdminUserId,
    configuration.Identity.EnvironmentAdminKeyId);
IdentitySnapshot fileIdentities = await identityProvider.LoadAsync(CancellationToken.None);
var authenticator = new ApiKeyAuthenticator(
    configuration.ClientApiKey,
    RouterRuntime.CreateEnvironmentAdministrator(configuration),
    configuration.Identity.EnvironmentAdminKeyId,
    "Environment administrator",
    fileIdentities);
var statistics = new InMemoryStatisticsProvider(
    configuration.Statistics.HourlyRetentionHours,
    configuration.Statistics.DailyRetentionDays);
var usage = new UsageAccumulator(
    RouterRuntime.CreateRoutes(configuration),
    authenticator.Snapshot,
    configuration.EffectiveMaxConcurrentRequestsPerUser);
var runtime = new RouterRuntime(
    configPath,
    identityProvider,
    authenticator,
    usage,
    configuration);
var proxy = new ProxyService(
    authenticator,
    client,
    runtime,
    usage,
    configuration.EffectiveMaxConcurrentRequests);
var compatibility = new CompatibilityService(
    authenticator,
    runtime,
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
    runtime,
    app.Logger);
var sessions = new UiSessionStore(authenticator);
var configurations = new ConfigurationAdminService(configPath);
var ui = new AdminUiService(authenticator, identityAdmin, configurations, sessions, runtime, app.Logger);

app.MapGet("/", static () => Results.Text("Router Kely is running. Use /v1 as the OpenAI-compatible base path.\n"));
app.MapGet("/health/live", static () => Results.Text("{\"status\":\"ok\"}", "application/json"));
app.MapGet("/health/ready", static () => Results.Text("{\"status\":\"ready\"}", "application/json"));
if (string.Equals(
        Environment.GetEnvironmentVariable("ROUTERKELY_BENCHMARK_METRICS"),
        "true",
        StringComparison.OrdinalIgnoreCase))
{
    app.MapGet(
        "/internal/benchmark/allocated-bytes",
        static () => GC.GetTotalAllocatedBytes(true).ToString(CultureInfo.InvariantCulture));
}
app.MapGet("/v1/models", proxy.WriteModelsAsync);
app.MapGet("/models", proxy.WriteModelsAsync);
app.MapGet("/v1/model/info", compatibility.WriteModelInfoAsync);
app.MapGet("/model/info", compatibility.WriteModelInfoAsync);
app.MapGet("/key/info", compatibility.WriteKeyInfoAsync);
app.MapGet("/user/daily/activity", compatibility.WriteDailyActivityAsync);
app.MapPost("/v1/chat/completions", proxy.ProxyChatCompletionsAsync);
app.MapPost("/chat/completions", proxy.ProxyChatCompletionsAsync);
app.MapGet("/ui", ui.RootAsync);
app.MapGet(AdminUiService.StylesheetPath, AdminUiService.StylesheetAsync);
app.MapGet("/ui/login", ui.LoginPageAsync);
app.MapPost("/ui/login", ui.LoginAsync);
app.MapPost("/ui/logout", ui.LogoutAsync);
app.MapGet("/ui/admin/users", ui.UsersAsync);
app.MapGet("/ui/admin/users/new", ui.NewUserAsync);
app.MapGet("/ui/admin/users/{id:long}", ui.UserAsync);
app.MapPost("/ui/actions/users", ui.SaveUserAsync);
app.MapPost("/ui/actions/users/{id:long}/keys/create", ui.CreateKeyAsync);
app.MapGet("/ui/admin/config", ui.ConfigPageAsync);
app.MapPost("/ui/actions/config", ui.SaveConfigAsync);
app.MapPost(AdminUiService.ModelsActionPath, ui.EditModelsAsync);
app.MapPost("/ui/actions/keys/{id:long}/revoke", ui.RevokeKeyAsync);

await app.RunAsync();
return 0;

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
