using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using static Harness;

/// <summary>
/// Streaming phase: proves that response bytes reach the client while the upstream is still
/// producing them.
/// </summary>
/// <remarks>
/// A gateway that buffers the whole upstream response before forwarding it still delivers the right
/// bytes in the right order, so only the arrival timing can detect it. For an LLM router that is the
/// difference between token streaming and a client that appears frozen until the answer is complete,
/// which is why this is asserted rather than reported.
/// </remarks>
internal static class Streaming
{
    private const string ApiKey = "sk-rk-performance-streaming";
    private const string ConfigurationFileName = "router-kely.streaming.json";
    private const int ChunkCount = MockUpstream.StreamChunks;
    /// <summary>Half the scripted upstream duration: a buffering gateway cannot beat it.</summary>
    private const double FirstByteBudgetMilliseconds = ChunkCount * MockUpstream.StreamChunkDelayMilliseconds / 2d;
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<StreamingReport> RunAsync(string routerExecutable, string temporaryDirectory)
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
        using var timeout = new CancellationTokenSource(PhaseTimeout);
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
                maxConcurrentRequestsPerUser: 1);
            router = StartRouter(routerExecutable, configurationPath, "  streaming router: ");

            using HttpClient directClient = CreateClient();
            using HttpClient routerClient = CreateClient();
            await WaitUntilReadyAsync(routerClient, new Uri($"{routerUrl}/health/ready"), router);

            StreamTiming direct = await ReadStreamAsync(
                directClient,
                new Uri($"{mockUrl}/v1/chat/completions"),
                authorize: false,
                timeout.Token);
            StreamTiming routed = await ReadStreamAsync(
                routerClient,
                new Uri($"{routerUrl}/v1/chat/completions"),
                authorize: true,
                timeout.Token);

            return new StreamingReport(
                direct,
                routed,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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

    /// <summary>
    /// Reads the SSE response incrementally and records when each read returned bytes, which is what
    /// distinguishes a forwarded stream from a buffered one.
    /// </summary>
    private static async Task<StreamTiming> ReadStreamAsync(
        HttpClient client,
        Uri endpoint,
        bool authorize,
        CancellationToken cancellationToken)
    {
        byte[] requestBody = Encoding.UTF8.GetBytes(
            $"{{\"model\":\"{PerfAlias}\",\"stream\":true,\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}]}}");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(requestBody)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (authorize)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MockUpstream.StreamHeader));

        long started = Stopwatch.GetTimestamp();
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureStatus(response, HttpStatusCode.OK, "streaming request");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[4_096];
        using var body = new MemoryStream();
        double firstByteMilliseconds = -1;
        double previousReadMilliseconds = 0;
        double maxGapMilliseconds = 0;
        int reads = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            double nowMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (firstByteMilliseconds < 0)
                firstByteMilliseconds = nowMilliseconds;
            else
                maxGapMilliseconds = Math.Max(maxGapMilliseconds, nowMilliseconds - previousReadMilliseconds);

            previousReadMilliseconds = nowMilliseconds;
            reads++;
            body.Write(buffer, 0, read);
        }

        double totalMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string text = Encoding.UTF8.GetString(body.ToArray());
        int events = text.Split("data: ").Length - 1;
        if (!text.Contains(MockUpstream.StreamDone, StringComparison.Ordinal) || events != ChunkCount + 2)
            throw new InvalidOperationException(
                $"The SSE body is incomplete or coalesced: {events} events, done marker "
                + $"{(text.Contains(MockUpstream.StreamDone, StringComparison.Ordinal) ? "present" : "missing")}.");

        return new StreamTiming(firstByteMilliseconds, totalMilliseconds, maxGapMilliseconds, reads);
    }

    /// <summary>Byte-arrival timing of one streamed response. The checks live in <see cref="RunAsync"/> callers.</summary>
    internal readonly record struct StreamTiming(
        double FirstByteMilliseconds,
        double TotalMilliseconds,
        double MaxGapMilliseconds,
        int Reads)
    {
        /// <summary>True when the client saw bytes long before the upstream finished producing them.</summary>
        internal bool IsIncremental => FirstByteMilliseconds >= 0 && FirstByteMilliseconds < FirstByteBudgetMilliseconds;
    }

    internal readonly record struct StreamingReport(
        StreamTiming Direct,
        StreamTiming Routed,
        double ElapsedMilliseconds)
    {
        internal bool Passed => Routed.IsIncremental;
    }
}
