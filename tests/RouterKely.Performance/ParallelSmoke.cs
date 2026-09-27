using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Always-on parallel smoke. One request carries a unique nonce into the upstream, which echoes it
/// back as the response id, so a mismatch proves a response was delivered to the wrong caller while
/// the response count proves none was lost.
/// </summary>
/// <remarks>
/// The phase runs against its own router because the process-wide concurrency limit is bound to a
/// fixed resource and can only be set at startup. It saturates the limit deterministically (every
/// slot filled before the excess requests are sent), then soaks at and above the limit with a
/// scripted upstream lag. The whole phase is bounded to a few seconds so it runs on every commit.
/// </remarks>
internal static class ParallelSmoke
{
    private const int Slots = 32;
    private const int OverflowRequests = 8;
    private const int ExtraWorkers = 8;
    private const int LagMilliseconds = 100;
    private const int RejectionRetryMilliseconds = 25;
    private const int MinimumCompletionPercent = 60;
    /// <summary>A held request never completes, so this doubles as the "rejected without queueing" bound.</summary>
    private const int MaxRejectionMilliseconds = 1_000;
    private const string ApiKey = "sk-rk-performance-parallel";
    private const string ConfigurationFileName = "router-kely.parallel.json";
    private static readonly TimeSpan SoakDuration = TimeSpan.FromMilliseconds(1_200);
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<ParallelSmokeReport> RunAsync(
        string routerExecutable,
        string temporaryDirectory)
    {
        int mockPort = Harness.ReserveLoopbackPort();
        int routerPort;
        do
        {
            routerPort = Harness.ReserveLoopbackPort();
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
            mock = await MockUpstream.StartAsync(mockUrl, gate, LagMilliseconds);
            string configurationPath = Harness.WriteConfiguration(
                temporaryDirectory,
                ConfigurationFileName,
                routerUrl,
                mockUrl,
                ApiKey,
                maxConcurrentRequests: Slots,
                maxConcurrentRequestsPerUser: Slots);
            router = Harness.StartRouter(routerExecutable, configurationPath, "  parallel router: ");

            using HttpClient client = Harness.CreateClient(Slots + OverflowRequests + ExtraWorkers);
            var endpoint = new Uri($"{routerUrl}/v1/chat/completions");
            await Harness.WaitUntilReadyAsync(client, new Uri($"{routerUrl}/health/ready"), router);

            SaturationResult saturation = await SaturateAndOverflowAsync(client, endpoint, gate);
            SoakResult atCapacity = await SoakAsync(client, endpoint, gate, workers: Slots);
            SoakResult oversubscribed = await SoakAsync(client, endpoint, gate, workers: Slots + ExtraWorkers);

            string detail =
                $"PASS ({Slots} slots, {LagMilliseconds} ms lag, {SoakDuration.TotalMilliseconds:F0} ms soak: "
                + $"saturation {saturation.Admitted} admitted + {saturation.Rejected} rejected in "
                + $"{saturation.RejectionMilliseconds:F0} ms with upstream peak {saturation.Peak}; "
                + $"at capacity {atCapacity.Completed} completed / {atCapacity.Rejected} rejected; "
                + $"oversubscribed {oversubscribed.Completed} completed / {oversubscribed.Rejected} rejected; "
                + $"all {saturation.Admitted + atCapacity.Completed + oversubscribed.Completed} responses matched their request nonce)";
            return new ParallelSmokeReport(detail, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
    /// Fills every slot, then proves the excess requests are rejected immediately and never reach
    /// the upstream, and that the slots are reusable once the held requests finish.
    /// </summary>
    private static async Task<SaturationResult> SaturateAndOverflowAsync(
        HttpClient client,
        Uri endpoint,
        ConcurrencyGate gate)
    {
        await gate.WaitForDrainAsync(GateTimeout);
        gate.Reset();

        var held = new List<Task<HttpResponseMessage>>(Slots);
        for (int index = 0; index < Slots; index++)
            held.Add(SendAsync(client, endpoint, Nonce(index), hold: true));

        await gate.WaitForActiveAsync(Slots, GateTimeout);

        long started = Stopwatch.GetTimestamp();
        HttpResponseMessage[] rejected;
        using (var overflowTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(MaxRejectionMilliseconds)))
        {
            Task<HttpResponseMessage>[] overflow = Enumerable.Range(0, OverflowRequests)
                .Select(index => SendAsync(client, endpoint, Nonce(Slots + index), hold: true, overflowTimeout.Token))
                .ToArray();
            try
            {
                rejected = await Task.WhenAll(overflow);
            }
            catch (OperationCanceledException)
            {
                // Held requests never complete, so an admitted excess request can only time out.
                throw new InvalidOperationException(
                    $"{OverflowRequests} requests above the {Slots}-slot process limit did not fail within "
                    + $"{MaxRejectionMilliseconds} ms, so they were admitted and left pending instead of being rejected.");
            }
        }

        double rejectionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        int admittedBeforeRelease = gate.Admitted;
        foreach (HttpResponseMessage response in rejected)
        {
            using (response)
            {
                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new InvalidOperationException(
                        $"A request above the {Slots}-slot process limit was not rejected: received {(int)response.StatusCode}.");
                await AssertProcessLimitAsync(response);
            }
        }

        if (admittedBeforeRelease != Slots)
            throw new InvalidOperationException(
                $"The upstream received {admittedBeforeRelease} requests while {Slots} were admitted, "
                + "so a rejected request reached the upstream.");

        if (gate.Peak != Slots)
            throw new InvalidOperationException(
                $"Upstream concurrency peaked at {gate.Peak} although exactly {Slots} slots were held.");

        gate.Release();
        HttpResponseMessage[] completed = await Task.WhenAll(held);
        for (int index = 0; index < completed.Length; index++)
        {
            using HttpResponseMessage response = completed[index];
            await VerifySuccessAsync(response, Nonce(index));
        }

        await gate.WaitForDrainAsync(GateTimeout);
        using HttpResponseMessage recovery = await SendAsync(client, endpoint, Nonce(999_999), hold: false);
        await VerifySuccessAsync(recovery, Nonce(999_999));

        return new SaturationResult(Slots, rejected.Length, rejectionMilliseconds, gate.Peak);
    }

    /// <summary>
    /// Keeps <paramref name="workers"/> closed-loop requests in flight for the soak duration. Every
    /// response must be a well-formed answer for the request that asked for it, and the upstream must
    /// never see more concurrent requests than the process limit.
    /// </summary>
    private static async Task<SoakResult> SoakAsync(
        HttpClient client,
        Uri endpoint,
        ConcurrencyGate gate,
        int workers)
    {
        await gate.WaitForDrainAsync(GateTimeout);
        gate.Reset();

        int completed = 0;
        int rejected = 0;
        long started = Stopwatch.GetTimestamp();
        var tasks = new Task[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            int workerIndex = worker;
            tasks[worker] = Task.Run(async () =>
            {
                int iteration = 0;
                while (Stopwatch.GetElapsedTime(started) < SoakDuration)
                {
                    string nonce = Nonce((workerIndex * 1_000) + iteration);
                    iteration++;
                    using HttpResponseMessage response = await SendAsync(client, endpoint, nonce, hold: false);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        await VerifySuccessAsync(response, nonce);
                        Interlocked.Increment(ref completed);
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await AssertProcessLimitAsync(response);
                        Interlocked.Increment(ref rejected);
                        // A rejection returns instantly, so an excess worker would otherwise spin
                        // at full speed and drown the phase in meaningless requests.
                        await Task.Delay(RejectionRetryMilliseconds);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Unexpected status {(int)response.StatusCode} during the parallel soak.");
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        await gate.WaitForDrainAsync(GateTimeout);
        int peak = gate.Peak;

        int expected = Slots * (int)(SoakDuration.TotalMilliseconds / LagMilliseconds) * MinimumCompletionPercent / 100;
        if (completed < expected)
            throw new InvalidOperationException(
                $"Only {completed} of an expected {expected}+ requests completed in {SoakDuration.TotalMilliseconds:F0} ms "
                + $"with {workers} workers, so concurrency slots were not released or reused.");

        if (gate.Admitted != completed)
            throw new InvalidOperationException(
                $"The upstream received {gate.Admitted} requests for {completed} completed responses, "
                + "so a rejected request reached the upstream or an admitted call was lost.");

        if (peak > Slots)
            throw new InvalidOperationException(
                $"Upstream concurrency peaked at {peak} above the {Slots}-slot process limit.");

        // With no more workers than slots, a rejection means a slot leaked rather than a genuine limit.
        if (workers <= Slots && rejected != 0)
            throw new InvalidOperationException(
                $"{rejected} of {workers} requests were rejected although the process limit is {Slots} slots.");

        if (workers > Slots && rejected == 0)
            throw new InvalidOperationException(
                $"{workers} workers against {Slots} slots were never rate limited.");

        return new SoakResult(completed, rejected, peak);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri endpoint,
        string nonce,
        bool hold,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(RequestBody(nonce))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        if (hold)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MockUpstream.HoldHeader));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static byte[] RequestBody(string nonce) =>
        Encoding.UTF8.GetBytes(
            $"{{\"model\":\"{Harness.PerfAlias}\",\"messages\":[{{\"role\":\"user\",\"content\":\"ping\"}}],\"user\":\"{nonce}\"}}");

    /// <summary>A fixed-width nonce so every request and response body has the same size.</summary>
    private static string Nonce(int value) => $"p{value:D8}";

    private static async Task VerifySuccessAsync(HttpResponseMessage response, string nonce)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"Expected 200 for {nonce}, received {(int)response.StatusCode}.");

        byte[] body = await response.Content.ReadAsByteArrayAsync();
        if (body.Length != MockUpstream.ResponseBytes)
            throw new InvalidOperationException(
                $"The response for {nonce} was {body.Length} bytes instead of {MockUpstream.ResponseBytes}.");

        using JsonDocument document = JsonDocument.Parse(body);
        string id = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
        if (!string.Equals(id, nonce, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The response id '{id}' does not match the request nonce '{nonce}', so a response was misrouted.");
    }

    private static async Task AssertProcessLimitAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        if (!body.Contains("\"code\":\"too_many_requests\"", StringComparison.Ordinal) ||
            !body.Contains("Process concurrency limit reached.", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The rejection is not the process concurrency limit error: {body}");
    }
}

internal readonly record struct ParallelSmokeReport(string Detail, double ElapsedMilliseconds);

internal readonly record struct SaturationResult(
    int Admitted,
    int Rejected,
    double RejectionMilliseconds,
    int Peak);

internal readonly record struct SoakResult(int Completed, int Rejected, int Peak);
