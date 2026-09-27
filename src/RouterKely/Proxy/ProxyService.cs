using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using RouterKely.Core.Authentication;
using RouterKely.Core.Concurrency;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;
using RouterKely.Runtime;

namespace RouterKely.Proxy;

public sealed class ProxyService
{
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly HttpClient _client;
    private readonly RouterRuntime _runtime;
    private readonly UsageAccumulator _usage;
    private readonly ConcurrencyLimiter _concurrency;

    public ProxyService(
        ApiKeyAuthenticator authenticator,
        HttpClient client,
        RouterRuntime runtime,
        UsageAccumulator usage,
        int maxConcurrentRequests)
    {
        _authenticator = authenticator;
        _client = client;
        _runtime = runtime;
        _usage = usage;
        _concurrency = new ConcurrencyLimiter(maxConcurrentRequests);
    }

    private bool TryAuthenticate(
        HttpContext context,
        out IdentityPrincipal? principal,
        out UsageAccount? account)
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization) &&
            authorization.Count == 1 &&
            _authenticator.TryAuthenticate(authorization[0].AsSpan(), out principal) &&
            principal is not null &&
            _usage.TryGetAccount(principal.Key.Id, out account))
            return true;

        principal = null;
        account = null;
        return false;
    }

    public async Task WriteModelsAsync(HttpContext context)
    {
        if (!TryAuthenticate(context, out _, out _))
        {
            await WriteErrorAsync(context, 401, "invalid_api_key", "Invalid API key.");
            return;
        }

        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("object", "list");
        writer.WriteStartArray("data");
        foreach (ModelRoute route in _runtime.Current.Routes)
        {
            writer.WriteStartObject();
            writer.WriteString("id", route.Alias);
            writer.WriteString("object", "model");
            writer.WriteNumber("created", 0);
            writer.WriteString("owned_by", "router-kely");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }

    public async Task ProxyChatCompletionsAsync(HttpContext context)
    {
        // One read per request: an administrator saving the configuration mid-request changes the
        // next request, never this one.
        RouterSnapshot settings = _runtime.Current;

        if (!TryAuthenticate(context, out _, out UsageAccount? account) || account is null)
        {
            await WriteErrorAsync(context, 401, "invalid_api_key", "Invalid API key.");
            return;
        }

        if (account.Quota.IsExceeded)
        {
            await WriteErrorAsync(context, 429, "quota_exceeded", "Daily quota exceeded.");
            return;
        }

        if (context.Request.ContentLength > settings.MaxRequestBodyBytes)
        {
            await WriteErrorAsync(context, 413, "request_too_large", "Request body is too large.");
            return;
        }

        string? contentType = context.Request.ContentType;
        if (contentType is null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, 415, "unsupported_media_type", "Content-Type must be application/json.");
            return;
        }

        if (!_concurrency.TryAcquire())
        {
            await WriteErrorAsync(context, 429, "too_many_requests", "Process concurrency limit reached.");
            return;
        }

        if (!account.Concurrency.TryAcquire())
        {
            _concurrency.Release();
            await WriteErrorAsync(context, 429, "too_many_requests", "User concurrency limit reached.");
            return;
        }

        try
        {
            await ProxyAdmittedAsync(context, account, contentType, settings);
        }
        finally
        {
            account.Concurrency.Release();
            _concurrency.Release();
        }
    }

    private async Task ProxyAdmittedAsync(
        HttpContext context,
        UsageAccount account,
        string contentType,
        RouterSnapshot settings)
    {
        ModelRewritingContent? content = await CreateContentAsync(context, settings);
        if (content is null)
            return;

        ModelRoute route = content.Route;
        long started = Stopwatch.GetTimestamp();

        using (content)
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.UpstreamBaseUri, "chat/completions")))
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.UpstreamApiKey);
            request.Headers.UserAgent.ParseAdd("router-kely/0.1");
            request.Content = content;
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            if (context.Request.Headers.TryGetValue("Accept", out var accept))
                request.Headers.TryAddWithoutValidation("Accept", accept.ToArray());

            try
            {
                using HttpResponseMessage upstream = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);

                context.Response.StatusCode = (int)upstream.StatusCode;
                CopyResponseHeaders(upstream, context.Response);
                UsageObservation observed = await CopyResponseAsync(
                    upstream.Content,
                    context.Response,
                    context.RequestAborted);
                bool success = upstream.IsSuccessStatusCode;
                UsageObservation accounted = success
                    ? observed
                    : new UsageObservation(0, 0, 0, true);
                long cost = UsageCostCalculator.Calculate(
                    accounted,
                    route.InputNanoUsdPerMillion,
                    route.CachedInputNanoUsdPerMillion,
                    route.OutputNanoUsdPerMillion);
                UsageOutcome outcome = success
                    ? UsageOutcome.Success
                    : upstream.StatusCode >= HttpStatusCode.InternalServerError
                        ? UsageOutcome.UpstreamError
                        : UsageOutcome.ClientError;
                account.Record(
                    route,
                    outcome,
                    accounted,
                    cost,
                    ElapsedMilliseconds(started));
            }
            catch (RequestBodyTooLargeException) when (!context.Response.HasStarted)
            {
                account.Record(route, UsageOutcome.ClientError, new UsageObservation(0, 0, 0, true), 0, ElapsedMilliseconds(started));
                await WriteErrorAsync(context, 413, "request_too_large", "Request body is too large.");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                account.Record(route, UsageOutcome.Cancelled, new UsageObservation(0, 0, 0, true), 0, ElapsedMilliseconds(started));
            }
            catch (HttpRequestException) when (!context.Response.HasStarted)
            {
                account.Record(route, UsageOutcome.UpstreamError, new UsageObservation(0, 0, 0, true), 0, ElapsedMilliseconds(started));
                await WriteErrorAsync(context, 502, "upstream_error", "Unable to reach the upstream API.");
            }
        }
    }

    private async Task<ModelRewritingContent?> CreateContentAsync(HttpContext context, RouterSnapshot settings)
    {
        byte[] prefix = ArrayPool<byte>.Shared.Rent(settings.MaxModelPrefixBytes);
        int length = 0;
        bool ownershipTransferred = false;
        try
        {
            while (length < settings.MaxModelPrefixBytes)
            {
                int read = await context.Request.Body.ReadAsync(
                    prefix.AsMemory(length, settings.MaxModelPrefixBytes - length),
                    context.RequestAborted);
                length += read;

                ModelScanStatus status = ModelPrefixScanner.Scan(
                    prefix.AsSpan(0, length),
                    read == 0,
                    settings.Routes,
                    out ModelRewrite rewrite);

                switch (status)
                {
                    case ModelScanStatus.Found:
                        ownershipTransferred = true;
                        return new ModelRewritingContent(
                            context.Request.Body,
                            prefix,
                            length,
                            rewrite,
                            settings.MaxRequestBodyBytes);
                    case ModelScanStatus.UnknownModel:
                        await WriteErrorAsync(context, 404, "model_not_found", "Unknown model.");
                        return null;
                    case ModelScanStatus.InvalidJson:
                    case ModelScanStatus.MissingModel:
                        await WriteErrorAsync(context, 400, "invalid_request", "A valid top-level model is required.");
                        return null;
                    case ModelScanStatus.NeedMoreData when read == 0:
                        await WriteErrorAsync(context, 400, "invalid_request", "A valid top-level model is required.");
                        return null;
                }
            }

            await WriteErrorAsync(context, 400, "invalid_request", "The model field exceeds the prefix limit.");
            return null;
        }
        finally
        {
            if (!ownershipTransferred)
                ArrayPool<byte>.Shared.Return(prefix);
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in upstream.Headers)
        {
            if (!IsHopByHop(header.Key))
                response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in upstream.Content.Headers)
        {
            if (!IsHopByHop(header.Key))
                response.Headers[header.Key] = header.Value.ToArray();
        }

        response.Headers.Remove("transfer-encoding");
    }

    private static async Task<UsageObservation> CopyResponseAsync(
        HttpContent content,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        var observer = new UsageStreamObserver();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                observer.Append(buffer.AsSpan(0, read));
                await response.BodyWriter.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return observer.Read();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long ElapsedMilliseconds(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static bool IsHopByHop(string name) =>
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            $"{{\"error\":{{\"message\":\"{message}\",\"type\":\"router_kely_error\",\"param\":null,\"code\":\"{code}\"}}}}",
            context.RequestAborted);
    }
}
