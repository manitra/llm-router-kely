using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

const string ApiKey = "sk-rk-performance-test";
const string AdminUiStylesheetPath = "/ui/assets/pico.classless-2.1.1.min.css";
const int ResponseBytes = 1_024;
const int DefaultWarmup = 100;
const int DefaultSamples = 1_000;
const long MaxIdleWorkingSetBytes = 100L * 1_024 * 1_024;
const long MaxBinaryBytes = 20L * 1_024 * 1_024;
const double MaxAllocatedBytesPerRequest = 8 * 1_024;
const int ExpectedPublishedFileCount = 1;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

string repositoryRoot = args.Length >= 1
    ? Path.GetFullPath(args[0])
    : FindRepositoryRoot();
string publishDirectory = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : throw new InvalidOperationException("Pass the Native AOT publish directory as the second argument.");
string routerExecutable = Path.Combine(
    publishDirectory,
    OperatingSystem.IsWindows() ? "RouterKely.exe" : "RouterKely");
if (!File.Exists(routerExecutable))
    throw new InvalidOperationException($"Publish Router Kely first. Missing '{routerExecutable}'.");

string[] publishedFiles = Directory.GetFiles(publishDirectory, "*", SearchOption.AllDirectories);
long binaryBytes = new FileInfo(routerExecutable).Length;
bool fileCountPass = publishedFiles.Length == ExpectedPublishedFileCount;
bool binarySizePass = binaryBytes <= MaxBinaryBytes;

int warmup = ReadPositiveInteger("ROUTERKELY_PERF_WARMUP", DefaultWarmup);
int samples = ReadPositiveInteger("ROUTERKELY_PERF_SAMPLES", DefaultSamples);
bool enforce = string.Equals(
    Environment.GetEnvironmentVariable("ROUTERKELY_PERF_ENFORCE"),
    "true",
    StringComparison.OrdinalIgnoreCase);

int mockPort = ReserveLoopbackPort();
int routerPort;
do
{
    routerPort = ReserveLoopbackPort();
}
while (routerPort == mockPort);
string mockUrl = $"http://127.0.0.1:{mockPort}";
string routerUrl = $"http://127.0.0.1:{routerPort}";
string temporaryDirectory = Path.Combine(
    repositoryRoot,
    "scripts",
    $".tmp-performance-{Environment.ProcessId}");
Directory.CreateDirectory(temporaryDirectory);

WebApplication? mock = null;
Process? router = null;
var slowRequest = new SlowRequestGate();
try
{
    mock = await StartMockUpstreamAsync(mockUrl, slowRequest);
    string configurationPath = WriteConfiguration(temporaryDirectory, routerUrl, mockUrl);
    router = StartRouter(routerExecutable, configurationPath);

    using var directClient = CreateClient();
    using var routerClient = CreateClient();
    using var concurrencyClient = CreateClient(maxConnectionsPerServer: 2);
    var directEndpoint = new Uri($"{mockUrl}/v1/chat/completions");
    var routerEndpoint = new Uri($"{routerUrl}/v1/chat/completions");
    byte[] directRequest = "{\"model\":\"deepseek-chat\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}"u8.ToArray();
    byte[] routerRequest = "{\"model\":\"perf\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}"u8.ToArray();

    await WaitUntilReadyAsync(routerClient, new Uri($"{routerUrl}/health/ready"), router);
    var allocationEndpoint = new Uri($"{routerUrl}/internal/benchmark/allocated-bytes");

    for (int index = 0; index < warmup; index++)
    {
        await MeasureAsync(directClient, directEndpoint, directRequest, authorize: false);
        await MeasureAsync(routerClient, routerEndpoint, routerRequest, authorize: true);
    }
    _ = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);
    long allocatedBytesBefore = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);

    var directMilliseconds = new double[samples];
    var routerMilliseconds = new double[samples];
    for (int index = 0; index < samples; index++)
    {
        if ((index & 1) == 0)
        {
            directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, authorize: false);
            routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, authorize: true);
        }
        else
        {
            routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, authorize: true);
            directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, authorize: false);
        }
    }

    Metrics direct = Metrics.Calculate(directMilliseconds);
    Metrics routed = Metrics.Calculate(routerMilliseconds);
    double overheadP50 = Math.Max(0, routed.P50 - direct.P50);
    double overheadP95 = Math.Max(0, routed.P95 - direct.P95);
    double overheadP99 = Math.Max(0, routed.P99 - direct.P99);
    double requestsPerSecond = samples / (routerMilliseconds.Sum() / 1_000d);
    long allocatedBytes = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint) - allocatedBytesBefore;
    double allocatedBytesPerRequest = (double)allocatedBytes / samples;
    bool allocationPass = allocatedBytesPerRequest <= MaxAllocatedBytesPerRequest;
    await Task.Delay(250);
    router.Refresh();
    long idleWorkingSetBytes = router.WorkingSet64;
    bool memoryPass = idleWorkingSetBytes < MaxIdleWorkingSetBytes;

    Console.WriteLine();
    Console.WriteLine("Router Kely end-to-end performance smoke");
    Console.WriteLine($"  runtime:    {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
    Console.WriteLine("  build:      Release Native AOT executable");
    Console.WriteLine($"  scenario:   {samples:N0} interleaved samples, {warmup:N0} warm-up, {ResponseBytes:N0}-byte response, concurrency 1");
    Console.WriteLine($"  direct:     p50 {direct.P50:F3} ms | p95 {direct.P95:F3} ms | p99 {direct.P99:F3} ms");
    Console.WriteLine($"  router:     p50 {routed.P50:F3} ms | p95 {routed.P95:F3} ms | p99 {routed.P99:F3} ms");
    Console.WriteLine($"  overhead:   p50 {overheadP50:F3} ms | p95 {overheadP95:F3} ms | p99 {overheadP99:F3} ms");
    Console.WriteLine($"  throughput: {requestsPerSecond:N0} sequential routed requests/s");
    Console.WriteLine($"  allocation: {allocatedBytesPerRequest:N0} B/routed request | limit <= {MaxAllocatedBytesPerRequest:N0} B => {(allocationPass ? "PASS" : "MISS")} (enforced)");
    Console.WriteLine($"  memory:     {FormatMiB(idleWorkingSetBytes)} MiB idle working set after load | limit < {FormatMiB(MaxIdleWorkingSetBytes)} MiB => {(memoryPass ? "PASS" : "MISS")} (enforced)");
    Console.WriteLine($"  binary:     {FormatMiB(binaryBytes)} MiB | limit <= {FormatMiB(MaxBinaryBytes)} MiB => {(binarySizePass ? "PASS" : "MISS")} (enforced)");
    Console.WriteLine($"  files:      {publishedFiles.Length:N0} published | required {ExpectedPublishedFileCount} => {(fileCountPass ? "PASS" : "MISS")} (enforced)");
    Console.WriteLine($"  constraint: p50 < 0.250 ms and p99 < 1.000 ms => {(overheadP50 < 0.250 && overheadP99 < 1.000 ? "PASS" : "MISS")}{(enforce ? " (enforced)" : " (informational)")}");

    await RunAdminUiSmokeAsync(routerUrl);
    Console.WriteLine("  admin UI:   PASS (login, create/edit user, create key, authenticate, revoke)");
    await RunConcurrencySmokeAsync(concurrencyClient, routerEndpoint, routerRequest, slowRequest);
    Console.WriteLine("  concurrency: PASS (per-user limit rejects immediately without queueing)");

    if (!allocationPass || !memoryPass || !binarySizePass || !fileCountPass)
        return 1;

    if (enforce && (overheadP50 >= 0.250 || overheadP99 >= 1.000))
        return 1;
}
finally
{
    if (router is not null && !router.HasExited)
    {
        router.Kill(entireProcessTree: true);
        await router.WaitForExitAsync();
    }

    router?.Dispose();
    if (mock is not null)
    {
        await mock.StopAsync();
        await mock.DisposeAsync();
    }

    if (Directory.Exists(temporaryDirectory))
        Directory.Delete(temporaryDirectory, recursive: true);
}

return 0;

static async Task<WebApplication> StartMockUpstreamAsync(string url, SlowRequestGate slowRequest)
{
    WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(url);
    WebApplication app = builder.Build();
    byte[] response = CreateResponseBody();

    app.MapPost("/v1/chat/completions", async context =>
    {
        await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
        if (context.Request.Headers.Accept == "application/x-router-kely-hold")
        {
            slowRequest.Started.TrySetResult();
            await slowRequest.Release.Task.WaitAsync(context.RequestAborted);
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = response.Length;
        await context.Response.Body.WriteAsync(response, context.RequestAborted);
    });

    await app.StartAsync();
    return app;
}

static byte[] CreateResponseBody()
{
    const string json = "{\"id\":\"perf\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_cache_hit_tokens\":0}}";
    byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
    if (jsonBytes.Length > ResponseBytes)
        throw new InvalidOperationException("Mock response exceeds its fixed size.");

    byte[] response = new byte[ResponseBytes];
    jsonBytes.CopyTo(response, 0);
    response.AsSpan(jsonBytes.Length).Fill((byte)' ');
    return response;
}

static string WriteConfiguration(string directory, string routerUrl, string mockUrl)
{
    string path = Path.Combine(directory, "router-kely.performance.json");
    var configuration = new
    {
        routerKely = new
        {
            listenUrl = routerUrl,
            clientApiKey = ApiKey,
            upstream = new
            {
                baseUrl = $"{mockUrl}/v1/",
                apiKey = "mock-upstream-key",
                allowInsecureLoopback = true
            },
            models = new[]
            {
                new
                {
                    alias = "perf",
                    upstreamModel = "deepseek-chat",
                    inputNanoUsdPerMillion = 1_000_000_000L,
                    cachedInputNanoUsdPerMillion = 100_000_000L,
                    outputNanoUsdPerMillion = 2_000_000_000L
                }
            },
            dailyQuotaNanoUsd = (long?)null,
            statistics = new
            {
                flushIntervalMilliseconds = 1_000,
                hourlyRetentionHours = 72,
                dailyRetentionDays = 7
            },
            maxRequestBodyBytes = 33_554_432,
            maxModelPrefixBytes = 65_536,
            maxConcurrentRequests = 2,
            maxConcurrentRequestsPerUser = 1
        }
    };

    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(configuration));
    return path;
}

static Process StartRouter(string executable, string configurationPath)
{
    var start = new ProcessStartInfo(executable)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    start.Environment["ROUTERKELY_CONFIG"] = configurationPath;
    start.Environment["ROUTERKELY_BENCHMARK_METRICS"] = "true";
    start.Environment["Logging__LogLevel__Default"] = "Warning";

    Process process = Process.Start(start)
        ?? throw new InvalidOperationException("Failed to start Router Kely.");
    process.OutputDataReceived += static (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
            Console.WriteLine($"  router: {eventArgs.Data}");
    };
    process.ErrorDataReceived += static (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
            Console.Error.WriteLine($"  router: {eventArgs.Data}");
    };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

static HttpClient CreateClient(int maxConnectionsPerServer = 1)
{
    var handler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        UseCookies = false,
        UseProxy = false
    };
    return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
}

static async Task RunAdminUiSmokeAsync(string routerUrl)
{
    var handler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        UseProxy = false
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    string origin = routerUrl;

    using (HttpResponseMessage page = await client.GetAsync($"{routerUrl}/ui/login"))
    {
        EnsureStatus(page, HttpStatusCode.OK, "login page");
        string loginHtml = await page.Content.ReadAsStringAsync();
        if (!loginHtml.Contains(AdminUiStylesheetPath, StringComparison.Ordinal) ||
            !loginHtml.Contains("<body><main>", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin UI login page does not use the vendored Pico stylesheet.");
        if (!page.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? policies))
            throw new InvalidOperationException("Admin UI login page is missing its Content-Security-Policy.");
        string policy = policies.Single();
        if (!policy.Contains("style-src 'self'", StringComparison.Ordinal) ||
            policy.Contains("unsafe-inline", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin UI stylesheet policy is not restricted to the same origin.");
    }

    using (HttpResponseMessage stylesheet = await client.GetAsync(routerUrl + AdminUiStylesheetPath))
    {
        EnsureStatus(stylesheet, HttpStatusCode.OK, "Pico stylesheet");
        if (stylesheet.Content.Headers.ContentType?.MediaType != "text/css" ||
            stylesheet.Content.Headers.ContentLength is not > 50_000)
            throw new InvalidOperationException("Admin UI Pico stylesheet response is invalid.");
    }

    using (var login = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/login"))
    {
        login.Headers.Add("Origin", origin);
        login.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = ApiKey });
        using HttpResponseMessage response = await client.SendAsync(login);
        EnsureStatus(response, HttpStatusCode.Redirect, "login");
    }

    string usersHtml;
    using (HttpResponseMessage users = await client.GetAsync($"{routerUrl}/ui/admin/users"))
    {
        EnsureStatus(users, HttpStatusCode.OK, "users page");
        usersHtml = await users.Content.ReadAsStringAsync();
    }

    string usersCsrf = ExtractBetween(usersHtml, "name=\"csrf\" value=\"", "\"");
    if (!usersHtml.Contains("/ui/admin/users/new"))
        throw new InvalidOperationException("Admin users list is missing the add-user link.");
    using (var saveUser = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/actions/users"))
    {
        saveUser.Headers.Add("Origin", origin);
        saveUser.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = usersCsrf,
            ["name"] = "Performance User",
            ["email"] = "performance@example.com",
            ["quotaUsd"] = "1",
            ["enabled"] = "true"
        });
        using HttpResponseMessage response = await client.SendAsync(saveUser);
        EnsureStatus(response, HttpStatusCode.Redirect, "create user");
    }

    string userHtml;
    using (HttpResponseMessage user = await client.GetAsync($"{routerUrl}/ui/admin/users/2"))
    {
        EnsureStatus(user, HttpStatusCode.OK, "user page");
        userHtml = await user.Content.ReadAsStringAsync();
    }

    string userCsrf = ExtractBetween(userHtml, "name=\"csrf\" value=\"", "\"");
    string keyHtml;
    using (var createKey = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/actions/users/2/keys/create"))
    {
        createKey.Headers.Add("Origin", origin);
        createKey.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = userCsrf,
            ["name"] = "Performance key"
        });
        using HttpResponseMessage response = await client.SendAsync(createKey);
        EnsureStatus(response, HttpStatusCode.OK, "create key");
        keyHtml = await response.Content.ReadAsStringAsync();
    }

    string plaintextKey = WebUtility.HtmlDecode(ExtractBetween(keyHtml, "<pre>", "</pre>"));
    using (var models = new HttpRequestMessage(HttpMethod.Get, $"{routerUrl}/v1/models"))
    {
        models.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        using HttpResponseMessage response = await client.SendAsync(models);
        EnsureStatus(response, HttpStatusCode.OK, "generated key authentication");
    }

    using (HttpResponseMessage user = await client.GetAsync($"{routerUrl}/ui/admin/users/2"))
    {
        EnsureStatus(user, HttpStatusCode.OK, "updated user page");
        userHtml = await user.Content.ReadAsStringAsync();
    }
    userCsrf = ExtractBetween(userHtml, "name=\"csrf\" value=\"", "\"");
    string keyId = ExtractBetween(userHtml, "action=\"/ui/actions/keys/", "/revoke\"");
    string revokePath = $"/ui/actions/keys/{keyId}/revoke";
    using (var revoke = new HttpRequestMessage(HttpMethod.Post, routerUrl + revokePath))
    {
        revoke.Headers.Add("Origin", origin);
        revoke.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf"] = userCsrf });
        using HttpResponseMessage response = await client.SendAsync(revoke);
        EnsureStatus(response, HttpStatusCode.Redirect, "revoke key");
    }

    using (var models = new HttpRequestMessage(HttpMethod.Get, $"{routerUrl}/v1/models"))
    {
        models.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        using HttpResponseMessage response = await client.SendAsync(models);
        EnsureStatus(response, HttpStatusCode.Unauthorized, "revoked key authentication");
    }

    using (var updateUser = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/actions/users"))
    {
        updateUser.Headers.Add("Origin", origin);
        updateUser.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["csrf"] = userCsrf,
            ["id"] = "2",
            ["name"] = "Performance User Edited",
            ["email"] = "performance@example.com",
            ["quotaUsd"] = "2",
            ["enabled"] = "true"
        });
        using HttpResponseMessage response = await client.SendAsync(updateUser);
        EnsureStatus(response, HttpStatusCode.Redirect, "update user");
    }

    using (HttpResponseMessage user = await client.GetAsync($"{routerUrl}/ui/admin/users/2"))
    {
        EnsureStatus(user, HttpStatusCode.OK, "edited user page");
        string editedHtml = await user.Content.ReadAsStringAsync();
        if (!editedHtml.Contains("value=\"Performance User Edited\"") || !editedHtml.Contains("value=\"2\""))
            throw new InvalidOperationException("Admin user edit form did not persist the update.");
    }

    using (HttpResponseMessage users = await client.GetAsync($"{routerUrl}/ui/admin/users"))
    {
        EnsureStatus(users, HttpStatusCode.OK, "edited users list");
        string editedListHtml = await users.Content.ReadAsStringAsync();
        if (!editedListHtml.Contains("href=\"/ui/admin/users/2\">Edit</a>") ||
            !editedListHtml.Contains("Performance User Edited"))
            throw new InvalidOperationException("Admin users list did not show the edited user and its edit link.");
    }
}

static async Task RunConcurrencySmokeAsync(
    HttpClient client,
    Uri endpoint,
    byte[] requestBody,
    SlowRequestGate slowRequest)
{
    Task<HttpResponseMessage> firstRequest = SendHoldRequestAsync(client, endpoint, requestBody);
    await slowRequest.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
        using HttpResponseMessage rejected = await SendHoldRequestAsync(client, endpoint, requestBody);
        EnsureStatus(rejected, HttpStatusCode.TooManyRequests, "per-user concurrency rejection");
    }
    finally
    {
        slowRequest.Release.TrySetResult();
    }

    using HttpResponseMessage admitted = await firstRequest;
    EnsureStatus(admitted, HttpStatusCode.OK, "admitted concurrent request");
}

static async Task<HttpResponseMessage> SendHoldRequestAsync(
    HttpClient client,
    Uri endpoint,
    byte[] requestBody)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
    {
        Content = new ByteArrayContent(requestBody)
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-router-kely-hold"));
    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
}

static void EnsureStatus(HttpResponseMessage response, HttpStatusCode expected, string operation)
{
    if (response.StatusCode != expected)
        throw new InvalidOperationException(
            $"Admin UI smoke failed during {operation}: expected {(int)expected}, received {(int)response.StatusCode}.");
}

static string ExtractBetween(string value, string start, string end)
{
    int startIndex = value.IndexOf(start, StringComparison.Ordinal);
    if (startIndex < 0)
        throw new InvalidOperationException($"Admin UI smoke could not find '{start}'.");
    startIndex += start.Length;
    int endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
    if (endIndex < 0)
        throw new InvalidOperationException($"Admin UI smoke could not find '{end}'.");
    return value[startIndex..endIndex];
}

static async Task WaitUntilReadyAsync(HttpClient client, Uri endpoint, Process router)
{
    long deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
    while (Stopwatch.GetTimestamp() < deadline)
    {
        if (router.HasExited)
            throw new InvalidOperationException($"Router Kely exited during startup with code {router.ExitCode}.");

        try
        {
            using HttpResponseMessage response = await client.GetAsync(endpoint);
            if (response.IsSuccessStatusCode)
                return;
        }
        catch (HttpRequestException)
        {
        }

        await Task.Delay(25);
    }

    throw new TimeoutException("Router Kely did not become ready within 10 seconds.");
}

static async Task<double> MeasureAsync(HttpClient client, Uri endpoint, byte[] requestBody, bool authorize)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
    {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Content = new ByteArrayContent(requestBody)
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    if (authorize)
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

    long started = Stopwatch.GetTimestamp();
    using HttpResponseMessage response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead);
    byte[] body = await response.Content.ReadAsByteArrayAsync();
    double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    if (response.StatusCode != HttpStatusCode.OK || body.Length != ResponseBytes)
        throw new InvalidOperationException(
            $"Unexpected response: {(int)response.StatusCode}, {body.Length} bytes.");

    return elapsedMilliseconds;
}

static async Task<long> ReadAllocatedBytesAsync(HttpClient client, Uri endpoint)
{
    string value = await client.GetStringAsync(endpoint);
    return long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static int ReadPositiveInteger(string name, int defaultValue)
{
    string? raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
        ? value
        : defaultValue;
}

static string FormatMiB(long bytes) =>
    (bytes / (1_024d * 1_024d)).ToString("F2", CultureInfo.InvariantCulture);

static string FindRepositoryRoot()
{
    for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "RouterKely.slnx")))
            return directory.FullName;
    }

    throw new InvalidOperationException("Could not locate RouterKely.slnx.");
}

readonly record struct Metrics(double P50, double P95, double P99)
{
    public static Metrics Calculate(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return new Metrics(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

sealed class SlowRequestGate
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
