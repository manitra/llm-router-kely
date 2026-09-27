using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouterKely.Configuration;

public sealed class LocalConfiguration
{
    /// <summary>Embedded copy of <c>config/router-kely.local.json.example</c>, written on first startup.</summary>
    internal const string DefaultTemplateResourceName = "RouterKely.Configuration.default-configuration.json";

    public RouterConfiguration RouterKely { get; init; } = new();

    public static LocalConfiguration Load(string path)
    {
        EnsureConfigurationFileExists(path);

        LocalConfiguration configuration = LoadRaw(path);
        configuration.RouterKely.ExpandEnvironmentReferences(Path.GetFullPath(path));
        configuration.RouterKely.Validate();
        return configuration;
    }

    public static LocalConfiguration LoadRaw(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Configuration file '{path}' was not found. Copy config/router-kely.local.json.example first.");

        LocalConfiguration configuration = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            LocalConfigurationJsonContext.Default.LocalConfiguration)
            ?? throw new InvalidOperationException("Configuration is empty.");

        configuration.RouterKely.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return configuration;
    }

    /// <summary>
    /// Creates the configuration file from the built-in default when it is absent, so a first
    /// start (or a reset that deletes it) yields an editable file referencing the secrets
    /// through environment variables instead of a startup failure.
    /// </summary>
    private static void EnsureConfigurationFileExists(string path)
    {
        if (File.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);
        byte[] template = ReadDefaultTemplate();
        using Stream resource = new MemoryStream(template);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using (FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(stream);
                stream.Flush(true);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            return; // Another process created it first.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Cannot create the configuration file '{fullPath}': {exception.Message}. "
                + "The directory is not writable by this user. Mount a Docker volume, or chown the "
                + "host directory to 1654:1654.",
                exception);
        }

        Console.Error.WriteLine(
            $"router-kely: created {fullPath} from the built-in default. "
            + "Edit it to set models, prices and quotas; the secrets it references come from the environment.");
    }

    private static byte[] ReadDefaultTemplate()
    {
        using Stream stream = typeof(LocalConfiguration).Assembly.GetManifestResourceStream(DefaultTemplateResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{DefaultTemplateResourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

public sealed class RouterConfiguration
{
    /// <summary>Upper bound on configurable model routes, shared by validation and the admin UI.</summary>
    public const int MaxModelRoutes = 64;

    public string ListenUrl { get; set; } = "http://127.0.0.1:8080";

    public string ClientApiKey { get; set; } = string.Empty;

    public UpstreamConfiguration Upstream { get; set; } = new();

    public IdentityConfiguration Identity { get; set; } = new();

    public ModelConfiguration[] Models { get; init; } = [];

    public long? DailyQuotaNanoUsd { get; init; }

    public StatisticsConfiguration Statistics { get; set; } = new();

    public int MaxRequestBodyBytes { get; init; } = 33_554_432;

    public int MaxModelPrefixBytes { get; init; } = 65_536;

    public int? MaxConcurrentRequests { get; init; }

    public int? MaxConcurrentRequestsPerUser { get; init; }

    internal int EffectiveMaxConcurrentRequests => MaxConcurrentRequests ?? 256;

    internal int EffectiveMaxConcurrentRequestsPerUser => MaxConcurrentRequestsPerUser ?? 32;

    internal void ExpandEnvironmentReferences(string sourcePath)
    {
        Identity ??= new IdentityConfiguration();
        Statistics ??= new StatisticsConfiguration();

        // Every reference is collected before anything is thrown, so a first failed startup
        // lists all the environment variables the operator has to define in one pass.
        var missing = new List<MissingEnvironmentVariable>();

        ClientApiKey = EnvironmentExpander.Expand(ClientApiKey, "RouterKely.ClientApiKey", missing);
        ListenUrl = EnvironmentExpander.Expand(ListenUrl, "RouterKely.ListenUrl", missing);
        Upstream = Upstream with
        {
            ApiKey = EnvironmentExpander.Expand(Upstream.ApiKey, "RouterKely.Upstream.ApiKey", missing),
            BaseUrl = EnvironmentExpander.Expand(Upstream.BaseUrl, "RouterKely.Upstream.BaseUrl", missing),
        };
        Identity = Identity with
        {
            FilePath = EnvironmentExpander.Expand(Identity.FilePath, "RouterKely.Identity.FilePath", missing),
            EnvironmentAdminName = EnvironmentExpander.Expand(Identity.EnvironmentAdminName, "RouterKely.Identity.EnvironmentAdminName", missing),
            EnvironmentAdminEmail = EnvironmentExpander.Expand(Identity.EnvironmentAdminEmail, "RouterKely.Identity.EnvironmentAdminEmail", missing),
        };
        for (int index = 0; index < Models.Length; index++)
        {
            ModelConfiguration model = Models[index];
            Models[index] = model with
            {
                Alias = EnvironmentExpander.Expand(model.Alias, $"RouterKely.Models[{index}].Alias", missing),
                UpstreamModel = EnvironmentExpander.Expand(model.UpstreamModel, $"RouterKely.Models[{index}].UpstreamModel", missing),
            };
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(BuildMissingVariablesMessage(sourcePath, missing));
    }

    private static string BuildMissingVariablesMessage(
        string sourcePath,
        List<MissingEnvironmentVariable> missing)
    {
        var builder = new StringBuilder();
        builder.Append("Configuration file '").Append(sourcePath)
            .AppendLine("' references environment variables that are not set. Define them and restart:");
        foreach (IGrouping<string, MissingEnvironmentVariable> group in missing
                     .GroupBy(item => item.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(group.Key).Append("  (referenced by ")
                .Append(string.Join(", ", group.Select(item => item.FieldPath).Distinct(StringComparer.Ordinal)))
                .AppendLine(")");
        }
        return builder.ToString().TrimEnd();
    }

    internal void ResolvePaths(string configurationDirectory)
    {
        Identity ??= new IdentityConfiguration();
        if (!Path.IsPathRooted(Identity.FilePath))
            Identity.FilePath = Path.Combine(configurationDirectory, Identity.FilePath);
    }

    internal void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("RouterKely.ListenUrl must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(ClientApiKey))
            throw new InvalidOperationException("RouterKely.ClientApiKey or ROUTERKELY_ADMIN_API_KEY is required.");
        if (!Uri.TryCreate(Upstream.BaseUrl, UriKind.Absolute, out Uri? upstream) ||
            (upstream.Scheme != Uri.UriSchemeHttps &&
             !(Upstream.AllowInsecureLoopback && upstream.Scheme == Uri.UriSchemeHttp && upstream.IsLoopback)))
            throw new InvalidOperationException(
                "RouterKely.Upstream.BaseUrl must use HTTPS unless insecure loopback is explicitly enabled.");
        if (string.IsNullOrWhiteSpace(Upstream.ApiKey))
            throw new InvalidOperationException("RouterKely.Upstream.ApiKey or ROUTERKELY_DEEPSEEK_API_KEY is required.");
        if (Models.Length is < 1 or > MaxModelRoutes)
            throw new InvalidOperationException($"Between 1 and {MaxModelRoutes} model routes must be configured.");
        if (Models.Any(model => string.IsNullOrWhiteSpace(model.Alias) || string.IsNullOrWhiteSpace(model.UpstreamModel)))
            throw new InvalidOperationException("Every model route requires Alias and UpstreamModel.");
        if (Models.Any(model =>
                model.InputNanoUsdPerMillion < 0 ||
                model.CachedInputNanoUsdPerMillion < 0 ||
                model.OutputNanoUsdPerMillion < 0))
            throw new InvalidOperationException("Model prices cannot be negative.");
        if (Models.Select(model => model.Alias).Distinct(StringComparer.Ordinal).Count() != Models.Length)
            throw new InvalidOperationException("Model aliases must be unique.");
        if (DailyQuotaNanoUsd < 0)
            throw new InvalidOperationException("DailyQuotaNanoUsd cannot be negative.");
        Identity.Validate();
        if (MaxModelPrefixBytes is < 1 or > 1_048_576)
            throw new InvalidOperationException("MaxModelPrefixBytes must be between 1 and 1048576.");
        if (MaxRequestBodyBytes < MaxModelPrefixBytes)
            throw new InvalidOperationException("MaxRequestBodyBytes must be greater than or equal to MaxModelPrefixBytes.");
        if (EffectiveMaxConcurrentRequests is < 1 or > 10_000)
            throw new InvalidOperationException("MaxConcurrentRequests must be between 1 and 10000.");
        if (EffectiveMaxConcurrentRequestsPerUser is < 1 ||
            EffectiveMaxConcurrentRequestsPerUser > EffectiveMaxConcurrentRequests)
            throw new InvalidOperationException(
                "MaxConcurrentRequestsPerUser must be between 1 and MaxConcurrentRequests.");
        Statistics.Validate();
    }
}

public sealed record IdentityConfiguration
{
    public string FilePath { get; set; } = "router-kely.identities.json";

    public int MaxUsers { get; init; } = 256;

    public int MaxKeys { get; init; } = 1_024;

    public long EnvironmentAdminUserId { get; init; } = 1;

    public long EnvironmentAdminKeyId { get; init; } = 1;

    public string EnvironmentAdminName { get; init; } = "Local Administrator";

    public string EnvironmentAdminEmail { get; init; } = "admin@localhost";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("Identity.FilePath is required.");
        if (MaxUsers is < 1 or > 10_000 || MaxKeys is < 1 or > 100_000)
            throw new InvalidOperationException("Identity limits are invalid.");
        if (EnvironmentAdminUserId <= 0 || EnvironmentAdminKeyId <= 0 ||
            string.IsNullOrWhiteSpace(EnvironmentAdminName) ||
            string.IsNullOrWhiteSpace(EnvironmentAdminEmail))
            throw new InvalidOperationException("Environment administrator metadata is invalid.");
    }
}

public sealed record UpstreamConfiguration
{
    public string BaseUrl { get; init; } = "https://api.deepseek.com/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public bool AllowInsecureLoopback { get; init; }
}

public sealed record ModelConfiguration
{
    public string Alias { get; init; } = string.Empty;

    public string UpstreamModel { get; init; } = string.Empty;

    public long InputNanoUsdPerMillion { get; init; }

    public long CachedInputNanoUsdPerMillion { get; init; }

    public long OutputNanoUsdPerMillion { get; init; }

    public int? MaxInputTokens { get; init; }

    public int? MaxOutputTokens { get; init; }

    public bool SupportsReasoning { get; init; }
}

public sealed class StatisticsConfiguration
{
    public int FlushIntervalMilliseconds { get; init; } = 1_000;

    public int HourlyRetentionHours { get; init; } = 72;

    public int DailyRetentionDays { get; init; } = 7;

    internal void Validate()
    {
        if (FlushIntervalMilliseconds is < 100 or > 60_000)
            throw new InvalidOperationException("Statistics.FlushIntervalMilliseconds must be between 100 and 60000.");
        if (HourlyRetentionHours is < 1 or > 168)
            throw new InvalidOperationException("Statistics.HourlyRetentionHours must be between 1 and 168.");
        if (DailyRetentionDays is < 1 or > 31)
            throw new InvalidOperationException("Statistics.DailyRetentionDays must be between 1 and 31.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocalConfiguration))]
internal sealed partial class LocalConfigurationJsonContext : JsonSerializerContext;
