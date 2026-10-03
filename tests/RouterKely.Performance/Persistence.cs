using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static Harness;

/// <summary>
/// Durable-statistics phase. It runs one router that writes daily usage to a store, drives a batch
/// of completions, restarts the process against the same store, and then proves three things: the
/// usage survived, the owning key's daily spend was restored, and the administration UI renders the
/// restored aggregates. It also reports allocated bytes per routed request, so persistence cannot
/// quietly add data-plane allocation.
/// </summary>
/// <remarks>
/// It always runs with its own router and a short flush interval, then waits past one tick before
/// stopping, because an abrupt kill skips the shutdown flush.
/// </remarks>
internal static class Persistence
{
    private const string ApiKey = "sk-rk-performance-persistence";
    private const string ConfigurationFileName = "router-kely.persistence.json";
    private const int WarmupRequests = 50;
    private const int MeasuredRequests = 200;
    private const int FlushMilliseconds = 100;

    private static readonly byte[] Request = Encoding.UTF8.GetBytes(
        $"{{\"model\":\"{PerfAlias}\",\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}]}}");

    internal static async Task<PersistenceReport> RunAsync(
        string routerExecutable,
        string temporaryDirectory)
    {
        string storeDirectory = Path.Combine(temporaryDirectory, "usage");
        int mockPort = ReserveLoopbackPort();
        int routerPort = ReserveLoopbackPort();
        string mockUrl = $"http://127.0.0.1:{mockPort}";
        string routerUrl = $"http://127.0.0.1:{routerPort}";
        var gate = new ConcurrencyGate();
        WebApplication? mock = null;
        long started = Stopwatch.GetTimestamp();
        try
        {
            mock = await MockUpstream.StartAsync(mockUrl, gate, delayMilliseconds: 0);
            string configurationPath = WriteConfiguration(
                temporaryDirectory,
                ConfigurationFileName,
                routerUrl,
                mockUrl,
                ApiKey,
                maxConcurrentRequests: 2,
                maxConcurrentRequestsPerUser: 1,
                statisticsDirectory: storeDirectory,
                statisticsFlushMilliseconds: FlushMilliseconds);

            double allocatedBytesPerRequest = await ProduceUsageAsync(
                routerExecutable,
                configurationPath,
                routerUrl);

            string[] dayFiles = Directory.GetFiles(storeDirectory, "*.json");
            if (dayFiles.Length == 0)
                throw new InvalidOperationException("No usage day file was written to the store.");

            long restoredSpend = await RestartAndVerifyAsync(
                routerExecutable,
                configurationPath,
                routerUrl);

            return new PersistenceReport(
                dayFiles.Length,
                restoredSpend,
                allocatedBytesPerRequest,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            if (mock is not null)
            {
                await mock.StopAsync();
                await mock.DisposeAsync();
            }
        }
    }

    private static async Task<double> ProduceUsageAsync(
        string routerExecutable,
        string configurationPath,
        string routerUrl)
    {
        Process? router = null;
        using HttpClient client = CreateClient();
        try
        {
            router = StartRouter(routerExecutable, configurationPath, "  persistence router: ");
            await WaitUntilReadyAsync(client, new Uri($"{routerUrl}/health/ready"), router);

            var endpoint = new Uri($"{routerUrl}/v1/chat/completions");
            var allocationEndpoint = new Uri($"{routerUrl}/internal/benchmark/allocated-bytes");
            for (int index = 0; index < WarmupRequests; index++)
                await MeasureAsync(client, endpoint, Request, true, ApiKey, MockUpstream.ResponseBytes);

            // Discard the first read so the endpoint's own page-in cost never lands in the delta.
            _ = await ReadAllocatedBytesAsync(client, allocationEndpoint);
            long allocatedBefore = await ReadAllocatedBytesAsync(client, allocationEndpoint);

            for (int index = 0; index < MeasuredRequests; index++)
                await MeasureAsync(client, endpoint, Request, true, ApiKey, MockUpstream.ResponseBytes);

            long allocated = await ReadAllocatedBytesAsync(client, allocationEndpoint) - allocatedBefore;

            // Wait past one flush tick so the usage batch reaches the store before this process dies.
            await Task.Delay(FlushMilliseconds * 4);
            return (double)allocated / MeasuredRequests;
        }
        finally
        {
            StopRouter(router);
        }
    }

    private static async Task<long> RestartAndVerifyAsync(
        string routerExecutable,
        string configurationPath,
        string routerUrl)
    {
        Process? router = null;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            router = StartRouter(routerExecutable, configurationPath, "  persistence router: ");
            await WaitUntilReadyAsync(client, new Uri($"{routerUrl}/health/ready"), router);

            // The restored spend must be charged to the owning key's current-day quota.
            using var keyInfo = new HttpRequestMessage(HttpMethod.Get, $"{routerUrl}/key/info");
            keyInfo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            using HttpResponseMessage keyResponse = await client.SendAsync(keyInfo);
            EnsureStatus(keyResponse, HttpStatusCode.OK, "key info after restart");
            using JsonDocument document = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            long spend = document.RootElement
                .GetProperty("info")
                .GetProperty("router_kely")
                .GetProperty("usage_nano_usd")
                .GetInt64();
            if (spend <= 0)
                throw new InvalidOperationException("Restored daily spend is zero after a restart.");

            // An unauthenticated browser must still be sent to the login page.
            using (var anonymous = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            }))
            {
                using HttpResponseMessage denied = await anonymous.GetAsync($"{routerUrl}/ui/admin/usage");
                EnsureStatus(denied, HttpStatusCode.Redirect, "usage page without a session");
                if (denied.Headers.Location?.OriginalString != "/ui/login")
                    throw new InvalidOperationException("Usage page did not redirect an unauthenticated request.");
            }

            using (var login = new HttpRequestMessage(HttpMethod.Post, $"{routerUrl}/ui/login"))
            {
                login.Headers.Add("Origin", routerUrl);
                login.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = ApiKey });
                using HttpResponseMessage response = await client.SendAsync(login);
                EnsureStatus(response, HttpStatusCode.Redirect, "login for the usage page");
            }

            using HttpResponseMessage usage = await client.GetAsync($"{routerUrl}/ui/admin/usage");
            EnsureStatus(usage, HttpStatusCode.OK, "usage page after restart");
            string html = await usage.Content.ReadAsStringAsync();
            if (!html.Contains("By user", StringComparison.Ordinal) ||
                html.Contains("No usage recorded", StringComparison.Ordinal) ||
                !html.Contains("/ui/admin/usage", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The usage page did not show the restored aggregates and its menu entry.");
            }

            return spend;
        }
        finally
        {
            StopRouter(router);
        }
    }

    private static void StopRouter(Process? router)
    {
        if (router is null)
            return;
        if (!router.HasExited)
        {
            router.Kill(entireProcessTree: true);
            router.WaitForExit();
        }
        router.Dispose();
    }

    internal readonly record struct PersistenceReport(
        int DayFileCount,
        long RestoredSpendNanoUsd,
        double AllocatedBytesPerRequest,
        double ElapsedMilliseconds);
}
