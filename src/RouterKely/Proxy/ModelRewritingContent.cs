using System.Buffers;
using System.Net;
using RouterKely.Core.Routing;

namespace RouterKely.Proxy;

internal sealed class ModelRewritingContent : HttpContent
{
    private readonly Stream _source;
    private byte[]? _prefix;
    private readonly int _prefixLength;
    private readonly ModelRewrite _rewrite;
    private readonly int _maxBodyBytes;

    public ModelRewritingContent(
        Stream source,
        byte[] prefix,
        int prefixLength,
        ModelRewrite rewrite,
        int maxBodyBytes)
    {
        _source = source;
        _prefix = prefix;
        _prefixLength = prefixLength;
        _rewrite = rewrite;
        _maxBodyBytes = maxBodyBytes;
    }

    public ModelRoute Route => _rewrite.Route;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        SerializeToStreamAsync(stream, cancellationToken);

    private async Task SerializeToStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = _prefix
            ?? throw new InvalidOperationException("Request content cannot be serialized more than once.");
        try
        {
            await stream.WriteAsync(
                prefix.AsMemory(0, _rewrite.ValueStart),
                cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(
                _rewrite.Route.ReplacementJsonUtf8,
                cancellationToken).ConfigureAwait(false);

            int suffixStart = _rewrite.ValueStart + _rewrite.ValueLength;
            await stream.WriteAsync(
                prefix.AsMemory(suffixStart, _prefixLength - suffixStart),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReturnPrefix();
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16_384);
        long totalBytes = _prefixLength;
        try
        {
            while (true)
            {
                int read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalBytes += read;
                if (totalBytes > _maxBodyBytes)
                    throw new RequestBodyTooLargeException();

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        ReturnPrefix();
        base.Dispose(disposing);
    }

    private void ReturnPrefix()
    {
        byte[]? prefix = Interlocked.Exchange(ref _prefix, null);
        if (prefix is not null)
            ArrayPool<byte>.Shared.Return(prefix);
    }
}

internal sealed class RequestBodyTooLargeException : Exception;
