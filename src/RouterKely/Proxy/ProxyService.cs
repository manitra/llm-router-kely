using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using RouterKely.Core.Authentication;
using RouterKely.Core.Routing;

namespace RouterKely.Proxy;

public sealed class ProxyService
{
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly HttpClient _client;
    private readonly Uri _upstreamBaseUri;
    private readonly string _upstreamApiKey;
    private readonly ModelRoute[] _routes;
    private readonly int _maxRequestBodyBytes;
    private readonly int _maxModelPrefixBytes;

    public ProxyService(
        ApiKeyAuthenticator authenticator,
        HttpClient client,
        Uri upstreamBaseUri,
        string upstreamApiKey,
        ModelRoute[] routes,
        int maxRequestBodyBytes,
        int maxModelPrefixBytes)
    {
        _authenticator = authenticator;
        _client = client;
        _upstreamBaseUri = upstreamBaseUri;
        _upstreamApiKey = upstreamApiKey;
        _routes = routes;
        _maxRequestBodyBytes = maxRequestBodyBytes;
        _maxModelPrefixBytes = maxModelPrefixBytes;
    }

    public bool Authenticate(HttpContext context) =>
        context.Request.Headers.TryGetValue("Authorization", out var authorization) &&
        authorization.Count == 1 &&
        _authenticator.Authenticate(authorization[0].AsSpan());

    public async Task WriteModelsAsync(HttpContext context)
    {
        if (!Authenticate(context))
        {
            await WriteErrorAsync(context, 401, "invalid_api_key", "Invalid API key.");
            return;
        }

        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("object", "list");
        writer.WriteStartArray("data");
        foreach (ModelRoute route in _routes)
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
        if (!Authenticate(context))
        {
            await WriteErrorAsync(context, 401, "invalid_api_key", "Invalid API key.");
            return;
        }

        if (context.Request.ContentLength > _maxRequestBodyBytes)
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

        ModelRewritingContent? content = await CreateContentAsync(context);
        if (content is null)
            return;

        using (content)
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_upstreamBaseUri, "chat/completions")))
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _upstreamApiKey);
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
                await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (RequestBodyTooLargeException) when (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, 413, "request_too_large", "Request body is too large.");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (HttpRequestException) when (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, 502, "upstream_error", "Unable to reach the upstream API.");
            }
        }
    }

    private async Task<ModelRewritingContent?> CreateContentAsync(HttpContext context)
    {
        byte[] prefix = ArrayPool<byte>.Shared.Rent(_maxModelPrefixBytes);
        int length = 0;
        bool ownershipTransferred = false;
        try
        {
            while (length < _maxModelPrefixBytes)
            {
                int read = await context.Request.Body.ReadAsync(
                    prefix.AsMemory(length, _maxModelPrefixBytes - length),
                    context.RequestAborted);
                length += read;

                ModelScanStatus status = ModelPrefixScanner.Scan(
                    prefix.AsSpan(0, length),
                    read == 0,
                    _routes,
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
                            _maxRequestBodyBytes);
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
