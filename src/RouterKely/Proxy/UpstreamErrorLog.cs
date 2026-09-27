using System.Text;

namespace RouterKely.Proxy;

/// <summary>
/// Renders why the upstream refused a request. The platform log view is the only place an
/// operator can see the reason, but a raw error body can be multi-line and unbounded, so the
/// snippet is capped and flattened to a single line. Request bodies, headers and secrets never
/// reach the log: only this bounded excerpt of an upstream *error* response does.
/// </summary>
public static class UpstreamErrorLog
{
    /// <summary>Bytes captured from an error body before the rest is forwarded unbuffered.</summary>
    public const int CapturedBytes = 2_048;

    /// <summary>Longest snippet kept in the log line.</summary>
    public const int MaxChars = 512;

    public static string Describe(ReadOnlySpan<byte> utf8Body)
    {
        if (utf8Body.IsEmpty)
            return "(empty body)";

        string text = Encoding.UTF8.GetString(utf8Body);
        var snippet = new StringBuilder(Math.Min(text.Length, MaxChars) + 1);
        foreach (char character in text)
        {
            if (snippet.Length >= MaxChars)
            {
                snippet.Append('…');
                break;
            }

            snippet.Append(char.IsControl(character) ? ' ' : character);
        }

        return snippet.ToString();
    }
}
