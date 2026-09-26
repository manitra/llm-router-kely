using System.Text.RegularExpressions;

namespace RouterKely.Configuration;

/// <summary>
/// Expands <c>${VAR}</c> references in configuration string values against the process environment.
/// The syntax mirrors POSIX shell parameter expansion by reference: the literal token
/// <c>${NAME}</c> is replaced by the value of <c>NAME</c>; a missing variable is reported
/// with the exact field path so the operator can fix the deployment without reading code.
/// Escaping with a leading <c>$$</c> produces a literal <c>$</c>.
/// </summary>
public static partial class EnvironmentExpander
{
    [GeneratedRegex(@"\$\$\{|\$\$|\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    public static string Expand(string value, string fieldPath)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);

        MatchCollection matches = ReferenceRegex().Matches(value);
        if (matches.Count == 0)
            return value;

        var builder = new System.Text.StringBuilder(value.Length);
        int cursor = 0;
        foreach (Match match in matches)
        {
            builder.Append(value, cursor, match.Index - cursor);

            if (match.Value.StartsWith("$$", StringComparison.Ordinal))
            {
                builder.Append(match.Value[1..]);
                cursor = match.Index + match.Length;
                continue;
            }

            string name = match.Groups[1].Value;
            string? resolved = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(resolved))
                throw new InvalidOperationException(
                    $"Configuration value '{fieldPath}' references ${{{name}}} but that environment variable is not set.");

            builder.Append(resolved);
            cursor = match.Index + match.Length;
        }
        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }
}
