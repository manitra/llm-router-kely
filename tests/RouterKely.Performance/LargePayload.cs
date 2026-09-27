using System.Diagnostics;
using System.Net;
using System.Text;
using static Harness;

/// <summary>
/// Payload-scaling phase: the same routed path with a 1-KiB, a 50-KiB and a 100-KiB non-stream
/// response. A per-byte cost on the response path is invisible at 1 KiB, so this phase exists to
/// keep it honest, and its allocation numbers prove the copy path does not grow with the payload.
/// </summary>
/// <remarks>
/// It runs against its own router because the extra model rows would change the configuration the
/// admin-UI smoke renders and asserts on.
/// </remarks>
internal static class LargePayload
{
    private const int WarmupPerSize = 25;
    private const string ApiKey = "sk-rk-performance-large";
    private const string ConfigurationFileName = "router-kely.large-payload.json";
    private static readonly PayloadScenario[] Scenarios =
    [
        new("1k", PerfAlias, PerfUpstreamModel, MockUpstream.ResponseBytes),
        new("50k", "perf-large", MockUpstream.LargeUpstreamModel, MockUpstream.LargeResponseBytes),
        new("100k", "perf-huge", MockUpstream.HugeUpstreamModel, MockUpstream.HugeResponseBytes)
    ];

    internal static async Task<LargePayloadReport> RunAsync(
        string routerExecutable,
        string temporaryDirectory,
        int samples)
    {
        int mockPort = ReserveLoopbackPort();
        int routerPort;
        do
        {
            routerPort = ReserveLoopbackPort();
        }
        while (routerPort == mockPort);

        string mockUrl = $"http://127.0.0.1:{mockPort}";
        string routerUrl = $"http://127.0.0.1:{routerPort}";
        var gate = new ConcurrencyGate();
        WebApplication? mock = null;
        Process? router = null;
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
                Scenarios.Select(scenario => (scenario.Alias, scenario.UpstreamModel)).ToArray());
            router = StartRouter(routerExecutable, configurationPath, "  payload router: ");

            using HttpClient directClient = CreateClient();
            using HttpClient routerClient = CreateClient();
            var directEndpoint = new Uri($"{mockUrl}/v1/chat/completions");
            var routerEndpoint = new Uri($"{routerUrl}/v1/chat/completions");
            var allocationEndpoint = new Uri($"{routerUrl}/internal/benchmark/allocated-bytes");
            await WaitUntilReadyAsync(routerClient, new Uri($"{routerUrl}/health/ready"), router);

            var metrics = new List<PayloadMetrics>(Scenarios.Length);
            foreach (PayloadScenario scenario in Scenarios)
            {
                metrics.Add(await MeasureScenarioAsync(
                    scenario,
                    directClient,
                    routerClient,
                    directEndpoint,
                    routerEndpoint,
                    allocationEndpoint,
                    samples));
            }

            return new LargePayloadReport(metrics, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
        }
    }

    private static async Task<PayloadMetrics> MeasureScenarioAsync(
        PayloadScenario scenario,
        HttpClient directClient,
        HttpClient routerClient,
        Uri directEndpoint,
        Uri routerEndpoint,
        Uri allocationEndpoint,
        int samples)
    {
        // The upstream model selects the response size, so the direct baseline and the routed call
        // exercise the identical payload.
        byte[] directRequest = Encoding.UTF8.GetBytes(
            $"{{\"model\":\"{scenario.UpstreamModel}\",\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}]}}");
        byte[] routerRequest = Encoding.UTF8.GetBytes(
            $"{{\"model\":\"{scenario.Alias}\",\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}]}}");

        for (int index = 0; index < WarmupPerSize; index++)
        {
            await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, scenario.Bytes);
            await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, scenario.Bytes);
        }

        // Discard the first read so the endpoint's own page-in cost never lands in the delta.
        _ = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);
        long allocatedBefore = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint);

        var directMilliseconds = new double[samples];
        var routerMilliseconds = new double[samples];
        for (int index = 0; index < samples; index++)
        {
            if ((index & 1) == 0)
            {
                directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, scenario.Bytes);
                routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, scenario.Bytes);
            }
            else
            {
                routerMilliseconds[index] = await MeasureAsync(routerClient, routerEndpoint, routerRequest, true, ApiKey, scenario.Bytes);
                directMilliseconds[index] = await MeasureAsync(directClient, directEndpoint, directRequest, false, ApiKey, scenario.Bytes);
            }
        }

        long allocatedBytes = await ReadAllocatedBytesAsync(routerClient, allocationEndpoint) - allocatedBefore;
        Metrics direct = Metrics.Calculate(directMilliseconds);
        Metrics routed = Metrics.Calculate(routerMilliseconds);
        return new PayloadMetrics(
            scenario.Label,
            scenario.Bytes,
            Math.Max(0, routed.P50 - direct.P50),
            Math.Max(0, routed.P95 - direct.P95),
            Math.Max(0, routed.P99 - direct.P99),
            (double)allocatedBytes / samples);
    }

    internal readonly record struct PayloadScenario(string Label, string Alias, string UpstreamModel, int Bytes);

    internal readonly record struct PayloadMetrics(
        string Label,
        int Bytes,
        double OverheadP50,
        double OverheadP95,
        double OverheadP99,
        double AllocatedBytesPerRequest);

    internal readonly record struct LargePayloadReport(
        IReadOnlyList<PayloadMetrics> Metrics,
        double ElapsedMilliseconds);
}
