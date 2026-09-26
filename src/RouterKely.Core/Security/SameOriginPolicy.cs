namespace RouterKely.Core.Security;

/// <summary>
/// Decides whether a state-changing form post was issued by the served application itself.
/// An opaque <c>Origin: null</c> (sandboxed iframes, webviews, extensions) falls back to the
/// Referer header, which is only sent for same-origin navigations thanks to the
/// <c>Referrer-Policy: same-origin</c> response header.
/// </summary>
public static class SameOriginPolicy
{
    public static bool IsSameOrigin(string scheme, string host, string? origin, string? referer)
    {
        string expected = $"{scheme}://{host}";
        if (origin is not null and not "null")
            return string.Equals(origin, expected, StringComparison.Ordinal);

        return referer is not null
            && referer.StartsWith(expected + "/", StringComparison.Ordinal);
    }
}
