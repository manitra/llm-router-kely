using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RouterKely.Configuration;

public sealed class ConfigurationAdminService
{
    private readonly string _path;
    private readonly object _gate = new();

    public ConfigurationAdminService(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public LocalConfiguration Load()
    {
        lock (_gate)
            return LocalConfiguration.Load(_path);
    }

    public LocalConfiguration Save(ConfigForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        lock (_gate)
        {
            LocalConfiguration current = LocalConfiguration.Load(_path);
            LocalConfiguration updated = ApplyForm(current, form);
            WriteAtomic(updated);
            return updated;
        }
    }

    private static LocalConfiguration ApplyForm(LocalConfiguration current, ConfigForm form)
    {
        var identity = new IdentityConfiguration
        {
            FilePath = current.RouterKely.Identity.FilePath,
            MaxUsers = ParseBoundedInt(form.IdentityMaxUsers, 1, 10_000, "Max users"),
            MaxKeys = ParseBoundedInt(form.IdentityMaxKeys, 1, 100_000, "Max keys"),
            EnvironmentAdminUserId = current.RouterKely.Identity.EnvironmentAdminUserId,
            EnvironmentAdminKeyId = current.RouterKely.Identity.EnvironmentAdminKeyId,
            EnvironmentAdminName = current.RouterKely.Identity.EnvironmentAdminName,
            EnvironmentAdminEmail = current.RouterKely.Identity.EnvironmentAdminEmail,
        };

        var upstream = new UpstreamConfiguration
        {
            BaseUrl = form.UpstreamBaseUrl.Trim(),
            ApiKey = current.RouterKely.Upstream.ApiKey,
            AllowInsecureLoopback = form.UpstreamAllowInsecureLoopback,
        };

        var models = new ModelConfiguration[form.Models.Length];
        for (int index = 0; index < form.Models.Length; index++)
        {
            ConfigModelForm entry = form.Models[index];
            models[index] = new ModelConfiguration
            {
                Alias = entry.Alias.Trim(),
                UpstreamModel = entry.UpstreamModel.Trim(),
                InputNanoUsdPerMillion = ParseNonNegativeLong(entry.InputUsdPerMillion, nameof(entry.InputUsdPerMillion)),
                CachedInputNanoUsdPerMillion = ParseNonNegativeLong(entry.CachedInputUsdPerMillion, nameof(entry.CachedInputUsdPerMillion)),
                OutputNanoUsdPerMillion = ParseNonNegativeLong(entry.OutputUsdPerMillion, nameof(entry.OutputUsdPerMillion)),
                MaxInputTokens = ParseOptionalPositiveInt(entry.MaxInputTokens, "Max input tokens"),
                MaxOutputTokens = ParseOptionalPositiveInt(entry.MaxOutputTokens, "Max output tokens"),
                SupportsReasoning = entry.SupportsReasoning,
            };
        }

        var statistics = new StatisticsConfiguration
        {
            FlushIntervalMilliseconds = ParseBoundedInt(form.StatisticsFlushMs, 100, 60_000, "Flush interval"),
            HourlyRetentionHours = ParseBoundedInt(form.StatisticsHourlyHours, 1, 168, "Hourly retention"),
            DailyRetentionDays = ParseBoundedInt(form.StatisticsDailyDays, 1, 31, "Daily retention"),
        };

        return new LocalConfiguration
        {
            RouterKely = new RouterConfiguration
            {
                ListenUrl = form.ListenUrl.Trim(),
                ClientApiKey = current.RouterKely.ClientApiKey,
                Upstream = upstream,
                Identity = identity,
                Models = models,
                DailyQuotaNanoUsd = ParseOptionalNonNegativeLong(form.DailyQuotaUsd, "Daily quota"),
                Statistics = statistics,
                MaxRequestBodyBytes = ParseBoundedInt(form.MaxRequestBodyBytes, 1, 1_073_741_824, "Max request body"),
                MaxModelPrefixBytes = ParseBoundedInt(form.MaxModelPrefixBytes, 1, 1_048_576, "Max model prefix"),
                MaxConcurrentRequests = ParseBoundedInt(form.MaxConcurrentRequests, 1, 10_000, "Max concurrent requests"),
                MaxConcurrentRequestsPerUser = ParseOptionalBoundedInt(form.MaxConcurrentRequestsPerUser, 1, 10_000, "Max concurrent per user"),
            },
        };
    }

    private void WriteAtomic(LocalConfiguration configuration)
    {
        // Validate eagerly so a write that would refuse to start is rejected here too.
        configuration.RouterKely.Validate();

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            configuration,
            LocalConfigurationJsonContext.Default.LocalConfiguration);

        string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(true);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    private static long ParseNonNegativeLong(string value, string field)
    {
        if (!long.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
            throw new InvalidOperationException($"{field} must be a non-negative integer.");
        return parsed;
    }

    private static long? ParseOptionalNonNegativeLong(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!long.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
            throw new InvalidOperationException($"{field} must be a non-negative integer or empty.");
        return parsed;
    }

    private static int ParseBoundedInt(string value, int min, int max, string field)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < min || parsed > max)
            throw new InvalidOperationException($"{field} must be an integer between {min} and {max}.");
        return parsed;
    }

    private static int? ParseOptionalBoundedInt(string? value, int min, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ParseBoundedInt(value, min, max, field);
    }

    private static int? ParseOptionalPositiveInt(string? value, string field) =>
        ParseOptionalBoundedInt(value, 1, int.MaxValue, field);

    public static ConfigForm ToForm(LocalConfiguration configuration) => new()
    {
        ListenUrl = configuration.RouterKely.ListenUrl,
        UpstreamBaseUrl = configuration.RouterKely.Upstream.BaseUrl,
        UpstreamAllowInsecureLoopback = configuration.RouterKely.Upstream.AllowInsecureLoopback,
        IdentityMaxUsers = configuration.RouterKely.Identity.MaxUsers.ToString(CultureInfo.InvariantCulture),
        IdentityMaxKeys = configuration.RouterKely.Identity.MaxKeys.ToString(CultureInfo.InvariantCulture),
        DailyQuotaUsd = configuration.RouterKely.DailyQuotaNanoUsd is long nano
            ? (nano / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture)
            : string.Empty,
        MaxRequestBodyBytes = configuration.RouterKely.MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture),
        MaxModelPrefixBytes = configuration.RouterKely.MaxModelPrefixBytes.ToString(CultureInfo.InvariantCulture),
        MaxConcurrentRequests = configuration.RouterKely.MaxConcurrentRequests?.ToString(CultureInfo.InvariantCulture)
            ?? configuration.RouterKely.EffectiveMaxConcurrentRequests.ToString(CultureInfo.InvariantCulture),
        MaxConcurrentRequestsPerUser = configuration.RouterKely.MaxConcurrentRequestsPerUser?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        StatisticsFlushMs = configuration.RouterKely.Statistics.FlushIntervalMilliseconds.ToString(CultureInfo.InvariantCulture),
        StatisticsHourlyHours = configuration.RouterKely.Statistics.HourlyRetentionHours.ToString(CultureInfo.InvariantCulture),
        StatisticsDailyDays = configuration.RouterKely.Statistics.DailyRetentionDays.ToString(CultureInfo.InvariantCulture),
        Models = configuration.RouterKely.Models
            .Select(model => new ConfigModelForm
            {
                Alias = model.Alias,
                UpstreamModel = model.UpstreamModel,
                InputUsdPerMillion = model.InputNanoUsdPerMillion.ToString(CultureInfo.InvariantCulture),
                CachedInputUsdPerMillion = model.CachedInputNanoUsdPerMillion.ToString(CultureInfo.InvariantCulture),
                OutputUsdPerMillion = model.OutputNanoUsdPerMillion.ToString(CultureInfo.InvariantCulture),
                MaxInputTokens = model.MaxInputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                MaxOutputTokens = model.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                SupportsReasoning = model.SupportsReasoning,
            })
            .ToArray(),
    };
}

public sealed class ConfigForm
{
    public string ListenUrl { get; set; } = string.Empty;
    public string UpstreamBaseUrl { get; set; } = string.Empty;
    public bool UpstreamAllowInsecureLoopback { get; set; }
    public string IdentityMaxUsers { get; set; } = "256";
    public string IdentityMaxKeys { get; set; } = "1024";
    public string? DailyQuotaUsd { get; set; }
    public string MaxRequestBodyBytes { get; set; } = "33554432";
    public string MaxModelPrefixBytes { get; set; } = "65536";
    public string MaxConcurrentRequests { get; set; } = "256";
    public string? MaxConcurrentRequestsPerUser { get; set; }
    public string StatisticsFlushMs { get; set; } = "1000";
    public string StatisticsHourlyHours { get; set; } = "72";
    public string StatisticsDailyDays { get; set; } = "7";
    public ConfigModelForm[] Models { get; set; } = [];
}

public sealed class ConfigModelForm
{
    public string Alias { get; set; } = string.Empty;
    public string UpstreamModel { get; set; } = string.Empty;
    public string InputUsdPerMillion { get; set; } = "0";
    public string CachedInputUsdPerMillion { get; set; } = "0";
    public string OutputUsdPerMillion { get; set; } = "0";
    public string? MaxInputTokens { get; set; }
    public string? MaxOutputTokens { get; set; }
    public bool SupportsReasoning { get; set; }
}
