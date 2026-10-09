using System.Net.Http.Headers;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;

namespace RouterKely.Runtime;

/// <summary>
/// Everything the request path needs, precomputed. Replaced as one object so a request never
/// observes a half-applied change and an in-flight request keeps the values it started with.
/// </summary>
public sealed record RouterSnapshot(
    RouterConfiguration Configuration,
    ModelRoute[] Routes,
    Uri UpstreamBaseUri,
    string UpstreamApiKey,
    int MaxRequestBodyBytes,
    int MaxModelPrefixBytes)
{
    /// <summary>Cached because a URI combination would allocate on every request.</summary>
    public Uri ChatCompletionsUri { get; } = new(UpstreamBaseUri, "chat/completions");

    /// <summary>Immutable and shared across requests; assigning it allocates nothing.</summary>
    public AuthenticationHeaderValue UpstreamAuthorization { get; } = new("Bearer", UpstreamApiKey);
}

/// <summary>
/// Holds the running configuration and re-applies it when an administrator saves a new one.
/// </summary>
/// <remarks>
/// A save re-reads and validates the file, then swaps this whole snapshot, so the change applies on
/// the next request with no restart and no dropped request. Settings baked into a fixed resource at
/// startup - the listener binding, the upstream connection pool and process concurrency limit, the
/// statistics flush timer and retention, and the identity provider file and capacity - cannot be
/// replaced while the process runs, so <see cref="ReloadAsync"/> reports those instead of leaving a
/// stale value silently in place.
/// </remarks>
public sealed class RouterRuntime
{
    private readonly string _configPath;
    private readonly IIdentityProvider _identityProvider;
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly UsageAccumulator _usage;
    private readonly RouterConfiguration _startup;
    private volatile RouterSnapshot _current;

    public RouterRuntime(
        string configPath,
        IIdentityProvider identityProvider,
        ApiKeyAuthenticator authenticator,
        UsageAccumulator usage,
        RouterConfiguration startup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(startup);

        _configPath = configPath;
        _identityProvider = identityProvider;
        _authenticator = authenticator;
        _usage = usage;
        _startup = startup;
        _current = Build(startup, CreateRoutes(startup));
    }

    public RouterSnapshot Current => _current;

    /// <summary>The users and keys currently accepted, including the environment administrator.</summary>
    public IdentitySnapshot Identities => _authenticator.Snapshot;

    public static ModelRoute[] CreateRoutes(RouterConfiguration configuration) => configuration.Models
        .Select((model, index) => new ModelRoute(
            index,
            model.Alias,
            model.UpstreamModel,
            model.InputNanoUsdPerMillion,
            model.CachedInputNanoUsdPerMillion,
            model.OutputNanoUsdPerMillion,
            model.MaxInputTokens,
            model.MaxOutputTokens,
            model.SupportsReasoning,
            model.SupportsVision))
        .ToArray();

    public static IdentityUser CreateEnvironmentAdministrator(RouterConfiguration configuration) => new(
        configuration.Identity.EnvironmentAdminUserId,
        configuration.Identity.EnvironmentAdminName,
        configuration.Identity.EnvironmentAdminEmail,
        IdentityRole.Admin,
        true,
        configuration.DailyQuotaNanoUsd,
        true);

    /// <summary>Rebuilds the key lookup and the usage accounts after the identity file changed.</summary>
    public void RefreshIdentities(IdentitySnapshot fileIdentities)
    {
        _authenticator.Update(fileIdentities);
        _usage.Update(_current.Routes, _authenticator.Snapshot);
    }

    /// <summary>
    /// Re-applies the configuration file to the running process and returns the labels of the saved
    /// settings that still need a restart. Nothing changes when the file is invalid.
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> ReloadAsync(CancellationToken cancellationToken)
    {
        // Load and validate first: a bad file must leave the running configuration untouched.
        RouterConfiguration next = LocalConfiguration.Load(_configPath).RouterKely;
        IdentitySnapshot fileIdentities = await _identityProvider.LoadAsync(cancellationToken);
        ModelRoute[] routes = CreateRoutes(next);
        IReadOnlyList<string> restartRequired = RestartRequired(_startup, next);

        _authenticator.Update(next.ClientApiKey, CreateEnvironmentAdministrator(next), fileIdentities);
        _usage.Update(routes, _authenticator.Snapshot);
        _current = Build(next, routes);
        return restartRequired;
    }

    private static RouterSnapshot Build(RouterConfiguration configuration, ModelRoute[] routes)
    {
        string baseUrl = configuration.Upstream.BaseUrl;
        return new RouterSnapshot(
            configuration,
            routes,
            new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + '/'),
            configuration.Upstream.ApiKey,
            configuration.MaxRequestBodyBytes,
            configuration.MaxModelPrefixBytes);
    }

    private static IReadOnlyList<string> RestartRequired(
        RouterConfiguration running,
        RouterConfiguration next)
    {
        List<string> fields = [];
        if (!string.Equals(running.ListenUrl, next.ListenUrl, StringComparison.Ordinal))
            fields.Add("Listen URL");
        if (running.EffectiveMaxConcurrentRequests != next.EffectiveMaxConcurrentRequests)
            fields.Add("Max concurrent requests");
        if (running.EffectiveMaxConcurrentRequestsPerUser != next.EffectiveMaxConcurrentRequestsPerUser)
            fields.Add("Max concurrent requests per user");
        if (!string.Equals(running.Identity.FilePath, next.Identity.FilePath, StringComparison.Ordinal) ||
            running.Identity.MaxUsers != next.Identity.MaxUsers ||
            running.Identity.MaxKeys != next.Identity.MaxKeys)
            fields.Add("Identity file and limits");
        if (running.Statistics.FlushIntervalMilliseconds != next.Statistics.FlushIntervalMilliseconds ||
            running.Statistics.HourlyRetentionHours != next.Statistics.HourlyRetentionHours ||
            running.Statistics.DailyRetentionDays != next.Statistics.DailyRetentionDays ||
            !string.Equals(
                running.Statistics.EffectivePersistenceDirectoryPath,
                next.Statistics.EffectivePersistenceDirectoryPath,
                StringComparison.Ordinal))
            fields.Add("Statistics retention, flush interval and persistence directory");
        return fields;
    }
}
