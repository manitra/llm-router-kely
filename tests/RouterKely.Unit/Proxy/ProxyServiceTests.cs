using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Statistics;
using RouterKely.Proxy;
using RouterKely.Runtime;
using Xunit;

namespace RouterKely.Unit.Proxy;

// The configuration file uses literal secrets, so this class needs no process environment variables.
public sealed class ProxyServiceTests : IDisposable
{
    private const string AdminKey = "sk-rk-admin-1";

    private const string RequestBody =
        """{"model":"fast","messages":[{"role":"user","content":"hi"}]}""";

    private const string UpstreamErrorBody =
        """{"error":{"message":"This model's maximum context length is 128000 tokens","type":"invalid_request_error"}}""";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"router-kely-proxy-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task UpstreamErrorIsForwardedVerbatimAndLoggedWithItsReason()
    {
        var upstream = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(UpstreamErrorBody, Encoding.UTF8, "application/json"),
        };
        (ProxyService proxy, RecordingLogger logger) = Compose(new StubHandler(upstream));
        DefaultHttpContext context = CreateRequest();

        await proxy.ProxyChatCompletionsAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(UpstreamErrorBody, ReadResponseBody(context));

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("400", message);
        Assert.Contains("fast", message);
        Assert.Contains("upstream.example", message);
        Assert.Contains("maximum context length", message);
    }

    [Fact]
    public async Task UnreachableUpstreamIsLoggedAndReportedAsBadGateway()
    {
        (ProxyService proxy, RecordingLogger logger) = Compose(new ThrowingHandler());
        DefaultHttpContext context = CreateRequest();

        await proxy.ProxyChatCompletionsAsync(context);

        Assert.Equal(502, context.Response.StatusCode);

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("fast", message);
    }

    [Fact]
    public void SnippetIsSingleLineAndBounded()
    {
        Assert.Equal("(empty body)", UpstreamErrorLog.Describe([]));
        Assert.Equal("a b", UpstreamErrorLog.Describe(Encoding.UTF8.GetBytes("a\nb")));

        string described = UpstreamErrorLog.Describe(Encoding.UTF8.GetBytes(new string('x', 900)));

        Assert.Equal(UpstreamErrorLog.MaxChars + 1, described.Length);
        Assert.EndsWith("…", described);
    }

    private (ProxyService Proxy, RecordingLogger Logger) Compose(HttpMessageHandler handler)
    {
        File.WriteAllText(_path, Config);
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
        var logger = new RecordingLogger();
        var proxy = new ProxyService(
            authenticator,
            new HttpClient(handler),
            runtime,
            usage,
            configuration.MaxConcurrentRequests ?? 256,
            logger);
        return (proxy, logger);
    }

    private static DefaultHttpContext CreateRequest()
    {
        byte[] body = Encoding.UTF8.GetBytes(RequestBody);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Headers.Authorization = $"Bearer {AdminKey}";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
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
                "upstreamModel": "deepseek-chat",
                "inputNanoUsdPerMillion": 1,
                "cachedInputNanoUsdPerMillion": 0,
                "outputNanoUsdPerMillion": 2
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

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused (upstream.example:443)");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

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
