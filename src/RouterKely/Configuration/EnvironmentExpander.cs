using System.Text.RegularExpressions;

namespace RouterKely.Configuration;

/// <summary>
/// Expands <c>${VAR}</c> references in configuration string values against the process environment.
/// The literal token <c>${NAME}</c> is replaced by the value of <c>NAME</c>. Escaping with a
/// leading <c>$$</c> produces a literal <c>$</c>.
///
/// An unresolved reference is recorded instead of thrown so a single startup can report
/// every missing variable at once; <see cref="LocalConfiguration.Load"/> raises the
/// consolidated error after walking the whole file.
/// </summary>
public static partial class EnvironmentExpander
{
    [GeneratedRegex(@"\$\$\{|\$\$|\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    public static string Expand(string value, string fieldPath, List<MissingEnvironmentVariable> missing)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldPath);
        ArgumentNullException.ThrowIfNull(missing);

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
            {
                // Recorded rather than thrown so one startup reports every missing variable.
                missing.Add(new MissingEnvironmentVariable(name, fieldPath));
                builder.Append(match.Value);
            }
            else
            {
                builder.Append(resolved);
            }
            cursor = match.Index + match.Length;
        }
        builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }
}

/// <param name="Name">The environment variable the configuration expects.</param>
/// <param name="FieldPath">The configuration field that references it.</param>
public sealed record MissingEnvironmentVariable(string Name, string FieldPath);
