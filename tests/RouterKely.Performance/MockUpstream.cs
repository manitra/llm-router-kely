using System.Buffers;
using System.Diagnostics;
using System.Text;

/// <summary>
/// Deterministic loopback upstream. It holds a request open on demand (so a caller can fill the
/// concurrency slots without relying on timing), can add a fixed scripted lag, and echoes the
/// request's <c>user</c> value back as the response id so a caller can prove each request was
/// answered by the upstream call it belongs to.
/// </summary>
internal static class MockUpstream
{
    internal const int ResponseBytes = 1_024;
    /// <summary>Payload-scaling sizes; the model name in the request selects one.</summary>
    internal const int LargeResponseBytes = 51_200;
    internal const int HugeResponseBytes = 102_400;
    internal const string LargeUpstreamModel = "deepseek-large";
    internal const string HugeUpstreamModel = "deepseek-huge";
    internal const string HoldHeader = "application/x-router-kely-hold";
    private const int MaxScannedBodyBytes = 4_096;
    private static readonly byte[] UserMarker = "\"user\":\""u8.ToArray();
    private static readonly byte[] ModelMarker = "\"model\":\""u8.ToArray();

    internal static async Task<WebApplication> StartAsync(
        string url,
        ConcurrencyGate gate,
        int delayMilliseconds)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        WebApplication app = builder.Build();

        app.MapPost("/v1/chat/completions", async context =>
        {
            (string id, string model) = await ReadRequestHeadAsync(context.Request.Body, context.RequestAborted);
            gate.Enter();
            try
            {
                if (context.Request.Headers.Accept == HoldHeader)
                    await gate.WaitForReleaseAsync(context.RequestAborted);
                else if (delayMilliseconds > 0)
                    await Task.Delay(delayMilliseconds, context.RequestAborted);

                byte[] response = CreateResponseBody(id, ResponseSizeFor(model));
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = response.Length;
                await context.Response.Body.WriteAsync(response, context.RequestAborted);
            }
            finally
            {
                gate.Exit();
            }
        });

        await app.StartAsync();
        return app;
    }

    private static int ResponseSizeFor(string model) => model switch
    {
        LargeUpstreamModel => LargeResponseBytes,
        HugeUpstreamModel => HugeResponseBytes,
        _ => ResponseBytes
    };

    /// <summary>
    /// Builds exactly <paramref name="size"/> bytes of valid completion JSON. The bulk sits inside
    /// the <c>content</c> string so the response-path cost scales with the payload instead of
    /// landing in trailing whitespace.
    /// </summary>
    internal static byte[] CreateResponseBody(string id, int size)
    {
        byte[] head = Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{id}\",\"object\":\"chat.completion\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"");
        byte[] tail = Encoding.UTF8.GetBytes(
            "\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_cache_hit_tokens\":0}}");
        int padding = size - head.Length - tail.Length;
        if (padding < 1)
            throw new InvalidOperationException($"Mock response size {size} is smaller than its envelope.");

        byte[] response = new byte[size];
        head.CopyTo(response, 0);
        response.AsSpan(head.Length, padding).Fill((byte)'a');
        tail.CopyTo(response, head.Length + padding);
        return response;
    }

    /// <summary>Reads the head of the body for the echoed id and the selected model, then drains the rest.</summary>
    private static async Task<(string Id, string Model)> ReadRequestHeadAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxScannedBodyBytes);
        int length = 0;
        try
        {
            while (length < buffer.Length)
            {
                int read = await body.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length),
                    cancellationToken);
                if (read == 0)
                    break;
                length += read;
            }

            ReadOnlySpan<byte> head = buffer.AsSpan(0, length);
            string id = ExtractValue(head, UserMarker, maxLength: 16, "none");
            string model = ExtractValue(head, ModelMarker, maxLength: 32, string.Empty);
            await body.CopyToAsync(Stream.Null, cancellationToken);
            return (id, model);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns the value of a <c>"name":"</c> marker, or <paramref name="fallback"/> when it is
    /// absent, empty, too long, or holds a character that could break the response envelope.
    /// </summary>
    private static string ExtractValue(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> marker,
        int maxLength,
        string fallback)
    {
        int start = body.IndexOf(marker);
        if (start < 0)
            return fallback;

        ReadOnlySpan<byte> rest = body[(start + marker.Length)..];
        int end = rest.IndexOf((byte)'"');
        if (end is < 1 || end > maxLength)
            return fallback;

        ReadOnlySpan<byte> value = rest[..end];
        foreach (byte character in value)
        {
            if (character is not ((>= (byte)'a' and <= (byte)'z') or
                (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_'))
                return fallback;
        }

        return Encoding.ASCII.GetString(value);
    }
}

/// <summary>
/// Upstream concurrency accounting plus the switch that releases held requests. <see cref="Peak"/>
/// is the invariant a concurrency limit must protect; <see cref="Admitted"/> proves that a rejected
/// request never reached the upstream.
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _peak;
    private int _admitted;

    internal int Active => Volatile.Read(ref _active);

    internal int Peak => Volatile.Read(ref _peak);

    internal int Admitted => Volatile.Read(ref _admitted);

    internal void Enter()
    {
        int active = Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _admitted);
        int peak = Volatile.Read(ref _peak);
        while (active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
            peak = Volatile.Read(ref _peak);
    }

    internal void Exit() => Interlocked.Decrement(ref _active);

    internal void Release() => _release.TrySetResult();

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
        _release.Task.WaitAsync(cancellationToken);

    /// <summary>Clears the counters for the next phase; the previous phase must have drained.</summary>
    internal void Reset()
    {
        if (Active != 0)
            throw new InvalidOperationException("Cannot reset the upstream gate while requests are in flight.");
        Volatile.Write(ref _peak, 0);
        Volatile.Write(ref _admitted, 0);
    }

    internal async Task WaitForActiveAsync(int expected, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Active < expected)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException(
                    $"Only {Active} of {expected} upstream requests became active within {timeout.TotalSeconds:F1} s.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    internal async Task WaitForDrainAsync(TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Active != 0)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"Upstream requests did not drain: {Active} still active.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
