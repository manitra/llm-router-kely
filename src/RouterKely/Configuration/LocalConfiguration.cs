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

    public ModelConfiguration[] Models { get; init; } = [];

    public int MaxRequestBodyBytes { get; init; } = 33_554_432;

    public int MaxModelPrefixBytes { get; init; } = 65_536;

    internal void ApplyEnvironmentOverrides()
    {
        ListenUrl = Environment.GetEnvironmentVariable("ROUTERKELY_LISTEN_URL") ?? ListenUrl;
        ClientApiKey = Environment.GetEnvironmentVariable("ROUTERKELY_ADMIN_API_KEY") ?? ClientApiKey;
        Upstream.ApiKey = Environment.GetEnvironmentVariable("ROUTERKELY_DEEPSEEK_API_KEY") ?? Upstream.ApiKey;
    }

    internal void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("RouterKely.ListenUrl must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(ClientApiKey))
            throw new InvalidOperationException("RouterKely.ClientApiKey or ROUTERKELY_ADMIN_API_KEY is required.");
        if (!Uri.TryCreate(Upstream.BaseUrl, UriKind.Absolute, out Uri? upstream) || upstream.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("RouterKely.Upstream.BaseUrl must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(Upstream.ApiKey))
            throw new InvalidOperationException("RouterKely.Upstream.ApiKey or ROUTERKELY_DEEPSEEK_API_KEY is required.");
        if (Models.Length == 0)
            throw new InvalidOperationException("At least one model route is required.");
        if (Models.Any(model => string.IsNullOrWhiteSpace(model.Alias) || string.IsNullOrWhiteSpace(model.UpstreamModel)))
            throw new InvalidOperationException("Every model route requires Alias and UpstreamModel.");
        if (Models.Select(model => model.Alias).Distinct(StringComparer.Ordinal).Count() != Models.Length)
            throw new InvalidOperationException("Model aliases must be unique.");
        if (MaxModelPrefixBytes is < 1 or > 1_048_576)
            throw new InvalidOperationException("MaxModelPrefixBytes must be between 1 and 1048576.");
        if (MaxRequestBodyBytes < MaxModelPrefixBytes)
            throw new InvalidOperationException("MaxRequestBodyBytes must be greater than or equal to MaxModelPrefixBytes.");
    }
}

public sealed class UpstreamConfiguration
{
    public string BaseUrl { get; init; } = "https://api.deepseek.com/v1/";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class ModelConfiguration
{
    public string Alias { get; init; } = string.Empty;

    public string UpstreamModel { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocalConfiguration))]
internal sealed partial class LocalConfigurationJsonContext : JsonSerializerContext;
