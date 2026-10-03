using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

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

    public LocalConfiguration LoadRaw()
    {
        lock (_gate)
            return LocalConfiguration.LoadRaw(_path);
    }

    public LocalConfiguration Save(ConfigForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        lock (_gate)
        {
            LocalConfiguration current = LocalConfiguration.LoadRaw(_path);
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
            ApiKey = form.UpstreamApiKey.Trim(),
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
            PersistenceDirectoryPath = NullIfBlank(form.StatisticsPersistenceDirectoryPath?.Trim() ?? string.Empty),
        };

        return new LocalConfiguration
        {
            RouterKely = new RouterConfiguration
            {
                ListenUrl = form.ListenUrl.Trim(),
                ClientApiKey = form.ClientApiKey.Trim(),
                Upstream = upstream,
                Identity = identity,
                Models = models,
                DailyQuotaNanoUsd = ParseOptionalQuotaUsd(form.DailyQuotaUsd),
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

    private static long? ParseOptionalQuotaUsd(string? value)
    {
        // The form field is a USD amount (see ToForm); convert it to the nanoUSD integer the
        // configuration stores, matching the per-user quota field on the users page.
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal usd) || usd < 0)
            throw new InvalidOperationException("Daily quota must be a non-negative USD amount or empty.");
        decimal nanoUsd = decimal.Ceiling(usd * 1_000_000_000m);
        if (nanoUsd > long.MaxValue)
            throw new InvalidOperationException("Daily quota is too large.");
        return (long)nanoUsd;
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
        ClientApiKey = configuration.RouterKely.ClientApiKey,
        UpstreamBaseUrl = configuration.RouterKely.Upstream.BaseUrl,
        UpstreamApiKey = configuration.RouterKely.Upstream.ApiKey,
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
        StatisticsPersistenceDirectoryPath = configuration.RouterKely.Statistics.PersistenceDirectoryPath,
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

    /// <summary>
    /// Parses the administration UI configuration form. Model rows are addressed by index
    /// (<c>models[0].alias</c>) rather than by parallel arrays, so any number of rows round-trips
    /// correctly even though browsers omit unchecked checkboxes from the posted form.
    /// </summary>
    public static ConfigForm FromForm(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new ConfigForm
        {
            ListenUrl = form["listenUrl"].ToString(),
            ClientApiKey = form["clientApiKey"].ToString(),
            UpstreamBaseUrl = form["upstreamBaseUrl"].ToString(),
            UpstreamApiKey = form["upstreamApiKey"].ToString(),
            UpstreamAllowInsecureLoopback = string.Equals(
                form["upstreamAllowInsecureLoopback"].ToString(),
                "true",
                StringComparison.Ordinal),
            IdentityMaxUsers = form["identityMaxUsers"].ToString(),
            IdentityMaxKeys = form["identityMaxKeys"].ToString(),
            DailyQuotaUsd = NullIfBlank(form["dailyQuotaUsd"].ToString()),
            MaxRequestBodyBytes = form["maxRequestBodyBytes"].ToString(),
            MaxModelPrefixBytes = form["maxModelPrefixBytes"].ToString(),
            MaxConcurrentRequests = form["maxConcurrentRequests"].ToString(),
            MaxConcurrentRequestsPerUser = NullIfBlank(form["maxConcurrentRequestsPerUser"].ToString()),
            StatisticsFlushMs = form["statisticsFlushMs"].ToString(),
            StatisticsHourlyHours = form["statisticsHourlyHours"].ToString(),
            StatisticsDailyDays = form["statisticsDailyDays"].ToString(),
            StatisticsPersistenceDirectoryPath = NullIfBlank(form["statisticsPersistenceDirectoryPath"].ToString()),
            Models = ParseModels(form),
        };
    }

    /// <summary>Appends one blank model row; returns false at the configured cap.</summary>
    public static bool TryAddModel(ConfigForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Models.Length >= RouterConfiguration.MaxModelRoutes)
            return false;
        form.Models = [.. form.Models, new ConfigModelForm()];
        return true;
    }

    /// <summary>Removes the row at <paramref name="index"/>; returns false out of range or for the last row.</summary>
    public static bool TryRemoveModel(ConfigForm form, int index)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (index < 0 || index >= form.Models.Length || form.Models.Length <= 1)
            return false;
        form.Models = [.. form.Models[..index], .. form.Models[(index + 1)..]];
        return true;
    }

    private const string ModelPrefix = "models[";
    private const string ModelAliasSuffix = "].alias";

    private static ConfigModelForm[] ParseModels(IFormCollection form)
    {
        // Rows may be sparse after a remove round-trip, so trust the indices the browser sent
        // instead of assuming a dense 0..n-1 range.
        var indices = new SortedSet<int>();
        foreach (string key in form.Keys)
        {
            if (TryParseModelIndex(key, out int index))
                indices.Add(index);
        }

        var models = new ConfigModelForm[indices.Count];
        int position = 0;
        foreach (int index in indices)
            models[position++] = ReadModel(form, index);
        return models;
    }

    private static bool TryParseModelIndex(string key, out int index)
    {
        index = -1;
        if (!key.StartsWith(ModelPrefix, StringComparison.Ordinal) ||
            !key.EndsWith(ModelAliasSuffix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> digits = key.AsSpan(
            ModelPrefix.Length,
            key.Length - ModelPrefix.Length - ModelAliasSuffix.Length);
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static ConfigModelForm ReadModel(IFormCollection form, int index)
    {
        string prefix = $"models[{index}].";
        return new ConfigModelForm
        {
            Alias = form[prefix + "alias"].ToString(),
            UpstreamModel = form[prefix + "upstreamModel"].ToString(),
            InputUsdPerMillion = form[prefix + "input"].ToString(),
            CachedInputUsdPerMillion = form[prefix + "cachedInput"].ToString(),
            OutputUsdPerMillion = form[prefix + "output"].ToString(),
            MaxInputTokens = NullIfBlank(form[prefix + "maxInput"].ToString()),
            MaxOutputTokens = NullIfBlank(form[prefix + "maxOutput"].ToString()),
            SupportsReasoning = string.Equals(
                form[prefix + "supportsReasoning"].ToString(),
                "true",
                StringComparison.Ordinal),
        };
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class ConfigForm
{
    public string ListenUrl { get; set; } = string.Empty;
    public string ClientApiKey { get; set; } = string.Empty;
    public string UpstreamBaseUrl { get; set; } = string.Empty;
    public string UpstreamApiKey { get; set; } = string.Empty;
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
    public string? StatisticsPersistenceDirectoryPath { get; set; }
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
