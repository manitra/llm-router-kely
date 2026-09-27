using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>
/// Process, client, port and configuration plumbing shared by every performance phase: the
/// sequential latency benchmark, the admin UI smoke, and the concurrency phases. A top-level
/// program reaches these through <c>using static Harness;</c>.
/// </summary>
internal static class Harness
{
    internal const string PerfAlias = "perf";
    internal const string PerfUpstreamModel = "deepseek-chat";
    internal const string MockUpstreamApiKey = "mock-upstream-key";

    internal static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static HttpClient CreateClient(int maxConnectionsPerServer = 1)
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

    internal static Process StartRouter(
        string executable,
        string configurationPath,
        string logPrefix = "  router: ")
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
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                Console.WriteLine($"{logPrefix}{eventArgs.Data}");
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                Console.Error.WriteLine($"{logPrefix}{eventArgs.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    internal static async Task WaitUntilReadyAsync(HttpClient client, Uri endpoint, Process router)
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

    internal static void EnsureStatus(HttpResponseMessage response, HttpStatusCode expected, string operation)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException(
                $"Performance harness failed during {operation}: expected {(int)expected}, received {(int)response.StatusCode}.");
    }

    internal static int ReadPositiveInteger(string name, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : defaultValue;
    }

    /// <summary>
    /// Writes a configuration whose only differences between phases are the numbers that bind a
    /// fixed resource: the ports and the concurrency limits.
    /// </summary>
    internal static string WriteConfiguration(
        string directory,
        string fileName,
        string routerUrl,
        string mockUrl,
        string clientApiKey,
        int maxConcurrentRequests,
        int maxConcurrentRequestsPerUser)
    {
        string path = Path.Combine(directory, fileName);
        var configuration = new
        {
            routerKely = new
            {
                listenUrl = routerUrl,
                clientApiKey,
                upstream = new
                {
                    baseUrl = $"{mockUrl}/v1/",
                    apiKey = MockUpstreamApiKey,
                    allowInsecureLoopback = true
                },
                models = new[]
                {
                    new
                    {
                        alias = PerfAlias,
                        upstreamModel = PerfUpstreamModel,
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
                maxConcurrentRequests,
                maxConcurrentRequestsPerUser
            }
        };

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(configuration));
        return path;
    }
}
