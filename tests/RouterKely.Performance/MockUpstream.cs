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
    internal const string HoldHeader = "application/x-router-kely-hold";
    private const int MaxScannedBodyBytes = 4_096;
    private static readonly byte[] UserMarker = "\"user\":\""u8.ToArray();

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
            string id = await ReadUserIdAsync(context.Request.Body, context.RequestAborted);
            gate.Enter();
            try
            {
                if (context.Request.Headers.Accept == HoldHeader)
                    await gate.WaitForReleaseAsync(context.RequestAborted);
                else if (delayMilliseconds > 0)
                    await Task.Delay(delayMilliseconds, context.RequestAborted);

                byte[] response = CreateResponseBody(id);
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

    internal static byte[] CreateResponseBody(string id)
    {
        string json = $"{{\"id\":\"{id}\",\"object\":\"chat.completion\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"ok\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_cache_hit_tokens\":0}}}}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        if (jsonBytes.Length > ResponseBytes)
            throw new InvalidOperationException("Mock response exceeds its fixed size.");

        byte[] response = new byte[ResponseBytes];
        jsonBytes.CopyTo(response, 0);
        response.AsSpan(jsonBytes.Length).Fill((byte)' ');
        return response;
    }

    /// <summary>Reads the head of the body for the caller-supplied id and drains the rest.</summary>
    private static async Task<string> ReadUserIdAsync(Stream body, CancellationToken cancellationToken)
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

            string id = ExtractUserId(buffer.AsSpan(0, length));
            await body.CopyToAsync(Stream.Null, cancellationToken);
            return id;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns the <c>user</c> value, or <c>none</c>. Only a short identifier is accepted so the
    /// echoed value can never turn the fixed response into invalid JSON.
    /// </summary>
    private static string ExtractUserId(ReadOnlySpan<byte> body)
    {
        int marker = body.IndexOf(UserMarker);
        if (marker < 0)
            return "none";

        ReadOnlySpan<byte> rest = body[(marker + UserMarker.Length)..];
        int end = rest.IndexOf((byte)'"');
        if (end is < 1 or > 16)
            return "none";

        ReadOnlySpan<byte> value = rest[..end];
        foreach (byte character in value)
        {
            if (character is not ((>= (byte)'a' and <= (byte)'z') or
                (>= (byte)'0' and <= (byte)'9') or (byte)'-'))
                return "none";
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
