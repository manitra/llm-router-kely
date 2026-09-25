using System.Net;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Routing;
using RouterKely.Proxy;

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
    .Select(model => new ModelRoute(model.Alias, model.UpstreamModel))
    .ToArray();

var proxy = new ProxyService(
    new ApiKeyAuthenticator(configuration.ClientApiKey),
    client,
    new Uri(configuration.Upstream.BaseUrl.EndsWith('/')
        ? configuration.Upstream.BaseUrl
        : configuration.Upstream.BaseUrl + '/'),
    configuration.Upstream.ApiKey,
    routes,
    configuration.MaxRequestBodyBytes,
    configuration.MaxModelPrefixBytes);

WebApplication app = builder.Build();

app.MapGet("/", static () => Results.Text("Router Kely is running. Use /v1 as the OpenAI-compatible base path.\n"));
app.MapGet("/health/live", static () => Results.Text("{\"status\":\"ok\"}", "application/json"));
app.MapGet("/health/ready", static () => Results.Text("{\"status\":\"ready\"}", "application/json"));
app.MapGet("/v1/models", proxy.WriteModelsAsync);
app.MapGet("/models", proxy.WriteModelsAsync);
app.MapPost("/v1/chat/completions", proxy.ProxyChatCompletionsAsync);
app.MapPost("/chat/completions", proxy.ProxyChatCompletionsAsync);

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
