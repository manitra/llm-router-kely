using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RouterKely.Core.Authentication;
using RouterKely.Core.Routing;
using RouterKely.Core.Statistics;
using RouterKely.Runtime;

namespace RouterKely.Compatibility;

public sealed class CompatibilityService
{
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly RouterRuntime _runtime;
    private readonly UsageAccumulator _usage;
    private readonly IStatisticsProvider _statistics;

    public CompatibilityService(
        ApiKeyAuthenticator authenticator,
        RouterRuntime runtime,
        UsageAccumulator usage,
        IStatisticsProvider statistics)
    {
        _authenticator = authenticator;
        _runtime = runtime;
        _usage = usage;
        _statistics = statistics;
    }

    public async Task WriteModelInfoAsync(HttpContext context)
    {
        if ((await AuthenticateAsync(context)).Principal is null)
            return;

        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteStartArray("data");
        foreach (ModelRoute route in _runtime.Current.Routes)
        {
            writer.WriteStartObject();
            writer.WriteString("model_name", route.Alias);
            writer.WriteStartObject("litellm_params");
            writer.WriteString("model", route.UpstreamModel);
            writer.WriteBoolean("use_in_pass_through", false);
            writer.WriteBoolean("use_litellm_proxy", false);
            writer.WriteBoolean("merge_reasoning_content_in_choices", false);
            writer.WriteEndObject();
            writer.WriteStartObject("model_info");
            writer.WriteString("id", StableModelId(route.Alias));
            writer.WriteBoolean("db_model", false);
            writer.WriteString("key", route.UpstreamModel);
            WriteNullableNumber(writer, "max_tokens", route.MaxOutputTokens);
            WriteNullableNumber(writer, "max_input_tokens", route.MaxInputTokens);
            WriteNullableNumber(writer, "max_output_tokens", route.MaxOutputTokens);
            writer.WriteNumber("input_cost_per_token", PerTokenUsd(route.InputNanoUsdPerMillion));
            writer.WriteNumber("cache_read_input_token_cost", PerTokenUsd(route.CachedInputNanoUsdPerMillion));
            writer.WriteNumber("output_cost_per_token", PerTokenUsd(route.OutputNanoUsdPerMillion));
            writer.WriteString("litellm_provider", "deepseek");
            writer.WriteString("mode", "chat");
            writer.WriteBoolean("supports_system_messages", true);
            writer.WriteBoolean("supports_function_calling", true);
            writer.WriteBoolean("supports_tool_choice", true);
            writer.WriteBoolean("supports_reasoning", route.SupportsReasoning);
            writer.WriteStartArray("supported_openai_params");
            writer.WriteStringValue("max_tokens");
            writer.WriteStringValue("stream");
            writer.WriteStringValue("stream_options");
            writer.WriteStringValue("temperature");
            writer.WriteStringValue("top_p");
            writer.WriteStringValue("tools");
            writer.WriteStringValue("tool_choice");
            writer.WriteStringValue("response_format");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }

    public async Task WriteKeyInfoAsync(HttpContext context)
    {
        AuthenticationContext authentication = await AuthenticateAsync(context);
        if (authentication.Principal is null || authentication.Account is null)
            return;

        IdentityPrincipal principal = authentication.Principal;
        long usageNanoUsd = authentication.Account.Quota.CurrentUsageNanoUsd;
        long? quotaNanoUsd = authentication.Account.Quota.QuotaNanoUsd;
        DateTime resetAt = DateTime.UtcNow.Date.AddDays(1);

        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteString("key", principal.Key.Masked);
        writer.WriteStartObject("info");
        writer.WriteString("token", principal.Key.Masked);
        writer.WriteNumber("key_id", principal.Key.Id);
        writer.WriteString("key_name", principal.Key.Name);
        writer.WriteString("key_alias", principal.Key.Masked);
        writer.WriteNumber("user_id", principal.User.Id);
        writer.WriteString("user_email", principal.User.Email);
        writer.WriteStartArray("models");
        foreach (ModelRoute route in _runtime.Current.Routes)
            writer.WriteStringValue(route.Alias);
        writer.WriteEndArray();
        writer.WriteNumber("spend", NanoUsdToUsd(usageNanoUsd));
        WriteNullableNumber(writer, "max_budget", quotaNanoUsd is long quota ? NanoUsdToUsd(quota) : null);
        writer.WriteString("budget_reset_at", resetAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteBoolean("blocked", false);
        writer.WriteStartObject("router_kely");
        writer.WriteString("quota_scope", "user");
        writer.WriteString("quota_period", "day");
        writer.WriteString("currency", "USD");
        writer.WriteNumber("usage_nano_usd", usageNanoUsd);
        WriteNullableNumber(writer, "quota_nano_usd", quotaNanoUsd);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }

    public async Task WriteDailyActivityAsync(HttpContext context)
    {
        AuthenticationContext authentication = await AuthenticateAsync(context);
        if (authentication.Principal is null)
            return;

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!TryReadDate(context, "start_date", today.AddDays(-29), out DateOnly startDate) ||
            !TryReadDate(context, "end_date", today, out DateOnly endDate) ||
            startDate > endDate || endDate.DayNumber - startDate.DayNumber > 366)
        {
            await WriteErrorAsync(context, 400, "invalid_request", "Invalid date range.");
            return;
        }

        StatisticsSnapshot snapshot = await _statistics.QueryAsync(
            startDate,
            endDate,
            authentication.Principal.User.Id,
            authentication.Principal.Key.Id,
            context.RequestAborted);

        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteStartArray("results");
        foreach (DailyUsage day in snapshot.Days)
            WriteDay(writer, day, authentication.Principal.Key.Masked);
        writer.WriteEndArray();
        writer.WriteStartObject("metadata");
        writer.WriteNumber("total_spend", NanoUsdToUsd(snapshot.Days.Sum(day => day.CostNanoUsd)));
        writer.WriteNumber("total_prompt_tokens", snapshot.Days.Sum(day => day.InputTokens));
        writer.WriteNumber("total_completion_tokens", snapshot.Days.Sum(day => day.OutputTokens));
        writer.WriteNumber("total_tokens", snapshot.Days.Sum(day => day.InputTokens + day.OutputTokens));
        writer.WriteNumber("total_api_requests", snapshot.Days.Sum(day => day.RequestCount));
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }

    private static void WriteDay(Utf8JsonWriter writer, DailyUsage day, string maskedKey)
    {
        writer.WriteStartObject();
        writer.WriteString("date", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteStartObject("metrics");
        WriteMetrics(writer, day.RequestCount, day.InputTokens, day.OutputTokens, day.CostNanoUsd);
        writer.WriteNumber("cache_read_input_tokens", day.CachedInputTokens);
        writer.WriteEndObject();
        writer.WriteStartObject("breakdown");
        writer.WriteStartObject("models");
        foreach (ModelDailyUsage model in day.Models)
        {
            writer.WriteStartObject(model.ModelAlias);
            WriteMetrics(writer, model.RequestCount, model.InputTokens, model.OutputTokens, model.CostNanoUsd);
            writer.WriteNumber("cache_read_input_tokens", model.CachedInputTokens);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteStartObject("providers");
        writer.WriteStartObject("deepseek");
        WriteMetrics(writer, day.RequestCount, day.InputTokens, day.OutputTokens, day.CostNanoUsd);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("api_keys");
            writer.WriteStartObject(maskedKey);
        WriteMetrics(writer, day.RequestCount, day.InputTokens, day.OutputTokens, day.CostNanoUsd);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMetrics(
        Utf8JsonWriter writer,
        long requests,
        long inputTokens,
        long outputTokens,
        long costNanoUsd)
    {
        writer.WriteNumber("spend", NanoUsdToUsd(costNanoUsd));
        writer.WriteNumber("prompt_tokens", inputTokens);
        writer.WriteNumber("completion_tokens", outputTokens);
        writer.WriteNumber("total_tokens", inputTokens + outputTokens);
        writer.WriteNumber("api_requests", requests);
    }

    private async ValueTask<AuthenticationContext> AuthenticateAsync(HttpContext context)
    {
        IdentityPrincipal? principal = null;
        UsageAccount? account = null;
        bool authenticated =
            context.Request.Headers.TryGetValue("Authorization", out var authorization) &&
            authorization.Count == 1 &&
            _authenticator.TryAuthenticate(authorization[0].AsSpan(), out principal) &&
            principal is not null &&
            _usage.TryGetAccount(principal.Key.Id, out account);
        if (authenticated)
            return new AuthenticationContext(principal, account);

        await WriteErrorAsync(context, 401, "invalid_api_key", "Invalid API key.");
        return default;
    }

    private static bool TryReadDate(
        HttpContext context,
        string name,
        DateOnly defaultValue,
        out DateOnly value)
    {
        string? raw = context.Request.Query[name];
        if (string.IsNullOrEmpty(raw))
        {
            value = defaultValue;
            return true;
        }

        return DateOnly.TryParseExact(
            raw,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    private static string StableModelId(string alias)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(alias), hash);
        return Convert.ToHexStringLower(hash);
    }

    private static double PerTokenUsd(long nanoUsdPerMillion) => nanoUsdPerMillion / 1_000_000_000_000_000d;

    private static double NanoUsdToUsd(long nanoUsd) => nanoUsd / 1_000_000_000d;

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is long number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is double number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            $"{{\"error\":{{\"message\":\"{message}\",\"type\":\"router_kely_error\",\"param\":null,\"code\":\"{code}\"}}}}",
            context.RequestAborted);
    }

    private readonly record struct AuthenticationContext(
        IdentityPrincipal? Principal,
        UsageAccount? Account);
}
