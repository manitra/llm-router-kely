using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouterKely.Configuration;

public sealed class LocalConfiguration
{
    public RouterConfiguration RouterKely { get; init; } = new();

    public static LocalConfiguration Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Configuration file '{path}' was not found. Copy config/router-kely.local.json.example first.");

        LocalConfiguration configuration = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            LocalConfigurationJsonContext.Default.LocalConfiguration)
            ?? throw new InvalidOperationException("Configuration is empty.");

        configuration.RouterKely.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(path))!);
        configuration.RouterKely.ApplyEnvironmentOverrides();
        configuration.RouterKely.Validate();
        return configuration;
    }
}

public sealed class RouterConfiguration
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:8080";

    public string ClientApiKey { get; set; } = string.Empty;

    public UpstreamConfiguration Upstream { get; init; } = new();

    public IdentityConfiguration Identity { get; set; } = new();

    public ModelConfiguration[] Models { get; init; } = [];

    public long? DailyQuotaNanoUsd { get; init; }

    public StatisticsConfiguration Statistics { get; set; } = new();

    public int MaxRequestBodyBytes { get; init; } = 33_554_432;

    public int MaxModelPrefixBytes { get; init; } = 65_536;

    internal void ApplyEnvironmentOverrides()
    {
        Identity ??= new IdentityConfiguration();
        Statistics ??= new StatisticsConfiguration();
        ListenUrl = Environment.GetEnvironmentVariable("ROUTERKELY_LISTEN_URL") ?? ListenUrl;
        ClientApiKey = Environment.GetEnvironmentVariable("ROUTERKELY_ADMIN_API_KEY") ?? ClientApiKey;
        Upstream.ApiKey = Environment.GetEnvironmentVariable("ROUTERKELY_DEEPSEEK_API_KEY") ?? Upstream.ApiKey;
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
        if (Models.Length == 0)
            throw new InvalidOperationException("At least one model route is required.");
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
        Statistics.Validate();
    }
}

public sealed class IdentityConfiguration
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

public sealed class UpstreamConfiguration
{
    public string BaseUrl { get; init; } = "https://api.deepseek.com/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public bool AllowInsecureLoopback { get; init; }
}

public sealed class ModelConfiguration
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
