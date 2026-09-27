using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using static Harness;

const string ApiKey = "sk-rk-performance-test";
const string AdminUiStylesheetPath = "/ui/assets/pico.classless-2.1.1.min.css";
const string AdminUiModelsActionPath = "/ui/actions/config/models";
const int ResponseBytes = MockUpstream.ResponseBytes;
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
var gate = new ConcurrencyGate();
try
{
    mock = await MockUpstream.StartAsync(mockUrl, gate, delayMilliseconds: 0);
    string configurationPath = WriteConfiguration(
        temporaryDirectory,
        "router-kely.performance.json",
        routerUrl,
        mockUrl,
        ApiKey,
        maxConcurrentRequests: 2,
        maxConcurrentRequestsPerUser: 1);
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
        await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, ResponseBytes);
        await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, ResponseBytes);
    }
    _ = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);
    long allocatedBytesBefore = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);

    var directMilliseconds = new double[samples];
    var routerMilliseconds = new double[samples];
    for (int index = 0; index < samples; index++)
    {
        if ((index & 1) == 0)
        {
            directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, ResponseBytes);
            routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, ResponseBytes);
        }
        else
        {
            routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, ResponseBytes);
            directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, ResponseBytes);
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

    LargePayload.LargePayloadReport payloads = await LargePayload.RunAsync(routerExecutable, temporaryDirectory, samples);
    bool payloadAllocationPass = payloads.Metrics.All(
        metric => metric.AllocatedBytesPerRequest <= MaxAllocatedBytesPerRequest);
    Console.WriteLine(
        "  payload:    overhead p50 "
        + string.Join(" | ", payloads.Metrics.Select(metric => $"{metric.Label} {metric.OverheadP50:F3} ms"))
        + " (informational)");
    Console.WriteLine(
        "  payload:    allocation "
        + string.Join(" | ", payloads.Metrics.Select(metric => $"{metric.Label} {metric.AllocatedBytesPerRequest:N0} B"))
        + $" | limit <= {MaxAllocatedBytesPerRequest:N0} B => {(payloadAllocationPass ? "PASS" : "MISS")} (enforced)");

    Streaming.StreamingReport streaming = await Streaming.RunAsync(routerExecutable, temporaryDirectory);
    Console.WriteLine(
        $"  streaming:  incremental {(streaming.Passed ? "PASS" : "MISS")} (enforced) | first byte routed "
        + $"{streaming.Routed.FirstByteMilliseconds:F1} ms of {streaming.Routed.TotalMilliseconds:F1} ms "
        + $"(direct {streaming.Direct.FirstByteMilliseconds:F1} ms of {streaming.Direct.TotalMilliseconds:F1} ms), "
        + $"longest gap routed {streaming.Routed.MaxGapMilliseconds:F1} ms, {streaming.Routed.Reads} reads");

    await RunAdminUiSmokeAsync(routerUrl, mockUrl);
    Console.WriteLine("  admin UI:   PASS (login, create/edit user, create key, authenticate, revoke, edit+save model list)");
    await RunConcurrencySmokeAsync(concurrencyClient, routerEndpoint, routerRequest, gate);
    Console.WriteLine("  concurrency: PASS (per-user limit rejects immediately without queueing)");

    ParallelSmokeReport parallel = await ParallelSmoke.RunAsync(routerExecutable, temporaryDirectory);
    Console.WriteLine($"  parallel:   {parallel.Detail} in {parallel.ElapsedMilliseconds / 1_000d:F1} s");

    if (!allocationPass || !memoryPass || !binarySizePass || !fileCountPass || !payloadAllocationPass || !streaming.Passed)
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

static async Task RunAdminUiSmokeAsync(string routerUrl, string mockUrl)
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

    string configHtml;
    using (HttpResponseMessage configPage = await client.GetAsync($"{routerUrl}/ui/admin/config"))
    {
        EnsureStatus(configPage, HttpStatusCode.OK, "config page");
        configHtml = await configPage.Content.ReadAsStringAsync();
        if (!configHtml.Contains("name=\"listenUrl\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin config page is missing the expected editor fields.");
        if (!configHtml.Contains("name=\"models[0].alias\"", StringComparison.Ordinal) ||
            !configHtml.Contains($"formaction=\"{AdminUiModelsActionPath}\"", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin config page has no indexed model row with an add control.");
        if (configHtml.Contains("Remove model", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin config page offers to remove the only model row.");
    }

    // "Add model" round-trips the whole form to a dedicated endpoint that re-renders without saving.
    using (var addModel = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}{AdminUiModelsActionPath}"))
    {
        addModel.Headers.Add("Origin", origin);
        addModel.Content = new FormUrlEncodedContent(ConfigForm(configHtml, routerUrl, mockUrl, "perf-two", action: "add"));
        using HttpResponseMessage response = await client.SendAsync(addModel);
        EnsureStatus(response, HttpStatusCode.OK, "add model row");
        string addedHtml = await response.Content.ReadAsStringAsync();
        if (!addedHtml.Contains("name=\"models[1].alias\"", StringComparison.Ordinal) ||
            !addedHtml.Contains("Model #2", StringComparison.Ordinal) ||
            !addedHtml.Contains("Model list updated", StringComparison.Ordinal) ||
            addedHtml.Contains("Configuration saved", StringComparison.Ordinal))
            throw new InvalidOperationException("Adding a model did not re-render an extra unsaved row.");
    }

    // Saving must reach the running process: the added alias answers on /v1/models immediately.
    using (var saveConfig = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/actions/config"))
    {
        saveConfig.Headers.Add("Origin", origin);
        saveConfig.Content = new FormUrlEncodedContent(ConfigForm(configHtml, routerUrl, mockUrl, "perf-two"));
        using HttpResponseMessage response = await client.SendAsync(saveConfig);
        EnsureStatus(response, HttpStatusCode.OK, "save configuration");
        string savedHtml = await response.Content.ReadAsStringAsync();
        if (!savedHtml.Contains("Configuration saved and applied. No restart needed", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin config save did not report an immediate apply.");
    }

    using (var models = new HttpRequestMessage(HttpMethod.Get, $"{routerUrl}/v1/models"))
    {
        models.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        using HttpResponseMessage response = await client.SendAsync(models);
        EnsureStatus(response, HttpStatusCode.OK, "model list after save");
        string modelsJson = await response.Content.ReadAsStringAsync();
        if (!modelsJson.Contains("\"perf-two\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The saved configuration is not live without a restart.");
    }
}

// Posts the whole configuration form, keeping every field at the value the running process was
// started with except the second model alias. Only settings that are fixed at startup are left
// unchanged, so a successful save reports no restart requirement.
static Dictionary<string, string> ConfigForm(
    string configHtml,
    string routerUrl,
    string mockUrl,
    string secondAlias,
    string? action = null)
{
    var fields = new Dictionary<string, string>
    {
        ["csrf"] = ExtractBetween(configHtml, "name=\"csrf\" value=\"", "\""),
        ["listenUrl"] = routerUrl,
        ["clientApiKey"] = ApiKey,
        ["upstreamBaseUrl"] = $"{mockUrl}/v1/",
        ["upstreamApiKey"] = Harness.MockUpstreamApiKey,
        ["upstreamAllowInsecureLoopback"] = "true",
        ["identityMaxUsers"] = "256",
        ["identityMaxKeys"] = "1024",
        ["maxRequestBodyBytes"] = "33554432",
        ["maxModelPrefixBytes"] = "65536",
        ["maxConcurrentRequests"] = "2",
        ["maxConcurrentRequestsPerUser"] = "1",
        ["statisticsFlushMs"] = "1000",
        ["statisticsHourlyHours"] = "72",
        ["statisticsDailyDays"] = "7",
        ["models[0].alias"] = Harness.PerfAlias,
        ["models[0].upstreamModel"] = Harness.PerfUpstreamModel,
        ["models[0].input"] = "1000000000",
        ["models[0].cachedInput"] = "100000000",
        ["models[0].output"] = "2000000000",
        ["models[1].alias"] = secondAlias,
        ["models[1].upstreamModel"] = "deepseek-reasoner",
        ["models[1].input"] = "1000000000",
        ["models[1].cachedInput"] = "0",
        ["models[1].output"] = "2000000000"
    };

    if (action is not null)
        fields["action"] = action;
    return fields;
}

static async Task RunConcurrencySmokeAsync(
    HttpClient client,
    Uri endpoint,
    byte[] requestBody,
    ConcurrencyGate gate)
{
    Task<HttpResponseMessage> firstRequest = SendHoldRequestAsync(client, endpoint, requestBody);
    await gate.WaitForActiveAsync(1, TimeSpan.FromSeconds(5));
    try
    {
        using HttpResponseMessage rejected = await SendHoldRequestAsync(client, endpoint, requestBody);
        EnsureStatus(rejected, HttpStatusCode.TooManyRequests, "per-user concurrency rejection");
    }
    finally
    {
        gate.Release();
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
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MockUpstream.HoldHeader));
    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
