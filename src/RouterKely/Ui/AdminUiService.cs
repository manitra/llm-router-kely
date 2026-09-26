using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Primitives;
using RouterKely.Configuration;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Security;
using RouterKely.Identity;

namespace RouterKely.Ui;

public sealed class AdminUiService
{
    public const string StylesheetPath = "/ui/assets/pico.classless-2.1.1.min.css";

    private const string StylesheetResourceName = "RouterKely.Ui.pico.classless-2.1.1.min.css";
    private const string StylesheetEtag = "\"sha256-61207a40ffc02a42d1e50143651c121beab70ed413c934c1ff84fa263ba436b0\"";
    private const string CrossOriginMessage = "Request rejected: it did not originate from this site. Open the UI directly and retry.";
    private const string RateLimitedMessage = "Too many sign-in attempts. Try again later.";
    private static readonly byte[] Stylesheet = LoadStylesheet();

    private readonly ApiKeyAuthenticator _authenticator;
    private readonly IdentityAdminService _identities;
    private readonly ConfigurationAdminService _configurations;
    private readonly UiSessionStore _sessions;
    private readonly ILogger _logger;
    private readonly LoginRateLimiter _loginRateLimiter = new();

    public AdminUiService(
        ApiKeyAuthenticator authenticator,
        IdentityAdminService identities,
        ConfigurationAdminService configurations,
        UiSessionStore sessions,
        ILogger logger)
    {
        _authenticator = authenticator;
        _identities = identities;
        _configurations = configurations;
        _sessions = sessions;
        _logger = logger;
    }

    public Task RootAsync(HttpContext context)
    {
        context.Response.Redirect("/ui/admin/users");
        return Task.CompletedTask;
    }

    public static Task StylesheetAsync(HttpContext context)
    {
        if (context.Request.Headers.IfNoneMatch == StylesheetEtag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "text/css; charset=utf-8";
        context.Response.ContentLength = Stylesheet.Length;
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers.ETag = StylesheetEtag;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return context.Response.Body.WriteAsync(Stylesheet, context.RequestAborted).AsTask();
    }

    public Task LoginPageAsync(HttpContext context) => WritePageAsync(
        context,
        "Sign in",
        """
        <h1>Router Kely</h1>
        <p>Paste an administrator API key. It is used only to create this in-memory session and is not stored.</p>
        <form method="post" action="/ui/login">
          <label>API key <input name="key" type="password" required autofocus autocomplete="off"></label>
          <button type="submit">Sign in</button>
        </form>
        """);

    public async Task LoginAsync(HttpContext context)
    {
        if (!IsSameOrigin(context))
        {
            await WriteStatusAsync(context, StatusCodes.Status403Forbidden, CrossOriginMessage);
            return;
        }

        if (!_loginRateLimiter.TryAcquire(context.Connection.RemoteIpAddress))
        {
            await Task.Delay(150, context.RequestAborted);
            await WriteStatusAsync(context, StatusCodes.Status429TooManyRequests, RateLimitedMessage);
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
        string token = form["key"].ToString();
        if (!_authenticator.TryAuthenticateToken(token.AsSpan(), out IdentityPrincipal? principal) ||
            principal is null || principal.User.Role != IdentityRole.Admin)
        {
            await Task.Delay(150, context.RequestAborted);
            await WriteStatusAsync(context, StatusCodes.Status401Unauthorized, "Invalid administrator key.");
            return;
        }

        UiSession session = _sessions.Create(principal);
        context.Response.Cookies.Append(
            UiSessionStore.CookieName,
            session.Id,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/ui",
                MaxAge = TimeSpan.FromHours(8),
                IsEssential = true
            });
        context.Response.Redirect("/ui/admin/users");
    }

    public async Task LogoutAsync(HttpContext context)
    {
        if (await RequireAdminPostAsync(context) is null)
            return;
        _sessions.Destroy(context);
        context.Response.Cookies.Delete(UiSessionStore.CookieName, new CookieOptions { Path = "/ui" });
        context.Response.Redirect("/ui/login");
    }

    public async Task UsersAsync(HttpContext context)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return;

        var html = new StringBuilder(4_096);
        html.Append("<h1>Users</h1>");
        html.Append("<table><thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Quota</th><th></th></tr></thead><tbody>");
        foreach (IdentityUser user in _identities.Snapshot.Users.OrderBy(user => user.Id))
        {
            html.Append("<tr><td>").Append(Encode(user.Name))
                .Append("</td><td>").Append(Encode(user.Email))
                .Append("</td><td>").Append(user.Role == IdentityRole.Admin ? "admin" : "user")
                .Append("</td><td>").Append(user.Enabled ? "enabled" : "disabled")
                .Append("</td><td>").Append(FormatQuota(user.QuotaNanoUsd)).Append("</td><td>");
            if (!user.IsEnvironment)
                html.Append("<a href=\"/ui/admin/users/").Append(user.Id).Append("\">Edit</a>");
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><p><a href=\"/ui/admin/users/new\" role=\"button\">Add user</a></p>");
        AppendAdminNav(html);
        AppendLogout(html, session!);
        await WritePageAsync(context, "Users", html.ToString());
    }

    public async Task NewUserAsync(HttpContext context)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return;
        await WriteUserFormAsync(context, session!, user: null);
    }

    public async Task UserAsync(HttpContext context, long id)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return;

        IdentityUser? user = _identities.Snapshot.Users.SingleOrDefault(user => user.Id == id && !user.IsEnvironment);
        if (user is null)
        {
            await WriteStatusAsync(context, StatusCodes.Status404NotFound, "User not found.");
            return;
        }

        await WriteUserFormAsync(context, session!, user);
    }

    private async Task WriteUserFormAsync(HttpContext context, UiSession session, IdentityUser? user)
    {
        string heading = user is null ? "Add user" : "Edit user";
        var html = new StringBuilder(4_096);
        html.Append("<p><a href=\"/ui/admin/users\">← Users</a></p><h1>").Append(heading).Append("</h1>");

        html.Append("<form method=\"post\" action=\"/ui/actions/users\">");
        AppendCsrf(html, session);
        if (user is not null)
            html.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(user.Id).Append("\">");
        html.Append("<label>Name <input name=\"name\" maxlength=\"100\" required value=\"")
            .Append(Encode(user?.Name ?? string.Empty)).Append("\"></label>");
        html.Append("<label>Email <input name=\"email\" type=\"email\" maxlength=\"254\" required value=\"")
            .Append(Encode(user?.Email ?? string.Empty)).Append("\"></label>");
        html.Append("<label>Daily quota USD <input name=\"quotaUsd\" type=\"number\" min=\"0\" step=\"0.000000001\" placeholder=\"unlimited\" value=\"")
            .Append(FormatQuotaInput(user?.QuotaNanoUsd)).Append("\"></label>");
        bool isEnabled = user?.Enabled ?? true;
        html.Append("<label>Status <select name=\"enabled\"><option value=\"true\"")
            .Append(isEnabled ? " selected" : string.Empty)
            .Append(">enabled</option><option value=\"false\"")
            .Append(isEnabled ? string.Empty : " selected")
            .Append(">disabled</option></select></label>");
        html.Append("<button type=\"submit\">").Append(user is null ? "Add user" : "Save changes").Append("</button></form>");
        if (user is null)
        {
            await WritePageAsync(context, heading, html.ToString());
            return;
        }

        html.Append("<h2>Keys</h2><table><thead><tr><th>Name</th><th>Key</th><th>Status</th><th></th></tr></thead><tbody>");
        foreach (IdentityKey key in _identities.Snapshot.Keys.Where(key => key.UserId == user.Id).OrderBy(key => key.Id))
        {
            html.Append("<tr><td>").Append(Encode(key.Name)).Append("</td><td><code>").Append(Encode(key.Masked))
                .Append("</code></td><td>").Append(key.Enabled ? "enabled" : "revoked").Append("</td><td>");
            if (key.Enabled)
            {
                html.Append("<form method=\"post\" action=\"/ui/actions/keys/").Append(key.Id).Append("/revoke\">");
                AppendCsrf(html, session);
                html.Append("<button type=\"submit\">Revoke</button></form>");
            }
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Add key</h2><form method=\"post\" action=\"/ui/actions/users/").Append(user.Id).Append("/keys/create\">");
        AppendCsrf(html, session);
        html.Append("<label>Name <input name=\"name\" maxlength=\"100\" required></label><button type=\"submit\">Generate key</button></form>");
        await WritePageAsync(context, heading, html.ToString());
    }

    public async Task SaveUserAsync(HttpContext context)
    {
        PostContext? post = await RequireAdminPostAsync(context);
        if (post is null)
            return;

        try
        {
            IFormCollection form = post.Form;
            long? id = ParseUserId(form["id"].ToString());
            long? quota = ParseQuota(form["quotaUsd"].ToString());
            bool enabled = string.Equals(form["enabled"].ToString(), "true", StringComparison.Ordinal);
            IdentityUser user = await _identities.SaveUserAsync(
                id,
                form["name"].ToString(),
                form["email"].ToString(),
                quota,
                enabled,
                context.RequestAborted);
            context.Response.Redirect($"/ui/admin/users/{user.Id}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    public async Task CreateKeyAsync(HttpContext context, long id)
    {
        PostContext? post = await RequireAdminPostAsync(context);
        if (post is null)
            return;

        try
        {
            IFormCollection form = post.Form;
            GeneratedApiKey generated = await _identities.CreateKeyAsync(
                id,
                form["name"].ToString(),
                context.RequestAborted);
            var html = new StringBuilder(1_024);
            html.Append("<h1>Key created</h1><p>Copy this key now. It cannot be shown again.</p><pre>")
                .Append(Encode(generated.Plaintext)).Append("</pre><p><a href=\"/ui/admin/users/").Append(id).Append("\">Continue</a></p>");
            await WritePageAsync(context, "Key created", html.ToString());
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    public async Task RevokeKeyAsync(HttpContext context, long id)
    {
        if (await RequireAdminPostAsync(context) is null)
            return;

        try
        {
            IdentityKey? key = _identities.Snapshot.Keys.SingleOrDefault(key => key.Id == id);
            await _identities.RevokeKeyAsync(id, context.RequestAborted);
            context.Response.Redirect(key is null ? "/ui/admin/users" : $"/ui/admin/users/{key.UserId}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    public async Task ConfigPageAsync(HttpContext context)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return;

        LocalConfiguration current;
        try
        {
            current = _configurations.Load();
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
            return;
        }

        ConfigForm form = ConfigurationAdminService.ToForm(current);
        await WriteConfigFormAsync(context, session!, form, saved: false);
    }

    public async Task SaveConfigAsync(HttpContext context)
    {
        PostContext? post = await RequireAdminPostAsync(context);
        if (post is null)
            return;

        ConfigForm form = ParseConfigForm(post.Form);
        try
        {
            _configurations.Save(form);
            _logger.LogInformation("Administrator updated the configuration file.");
            await WriteConfigFormAsync(context, post.Session, form, saved: true);
        }
        catch (InvalidOperationException exception)
        {
            await WriteConfigFormAsync(context, post.Session, form, saved: false, error: exception.Message);
        }
    }

    private async Task WriteConfigFormAsync(
        HttpContext context,
        UiSession session,
        ConfigForm form,
        bool saved,
        string? error = null)
    {
        var html = new StringBuilder(8_192);
        AppendAdminNav(html);
        html.Append("<h1>Configuration</h1>");
        html.Append("<p><small>Saved file: <code>")
            .Append(Encode(_configurations.FilePath))
            .Append("</code></small></p>");
        html.Append("<p><strong>Changes take effect after restart.</strong> "
            + "Secrets are read from environment variables, not this file.</p>");
        if (saved)
            html.Append("<p><mark>Configuration saved. Restart the router to apply.</mark></p>");
        if (error is not null)
            html.Append("<p><strong>").Append(Encode(error)).Append("</strong></p>");

        html.Append("<form method=\"post\" action=\"/ui/actions/config\">");
        AppendCsrf(html, session);
        html.Append("<fieldset><legend>Server</legend>");
        html.Append("<label>Listen URL <input name=\"listenUrl\" required value=\"")
            .Append(Encode(form.ListenUrl)).Append("\"></label>");
        html.Append("<label><input type=\"checkbox\" name=\"upstreamAllowInsecureLoopback\" value=\"true\"")
            .Append(form.UpstreamAllowInsecureLoopback ? " checked" : string.Empty)
            .Append("> Allow insecure loopback upstream</label>");
        html.Append("</fieldset>");

        html.Append("<fieldset><legend>Upstream</legend>");
        html.Append("<label>Base URL <input name=\"upstreamBaseUrl\" required value=\"")
            .Append(Encode(form.UpstreamBaseUrl)).Append("\"></label>");
        html.Append("<p><small>API key: <code>***</code> (read from ROUTERKELY_DEEPSEEK_API_KEY)</small></p>");
        html.Append("</fieldset>");

        html.Append("<fieldset><legend>Identity</legend>");
        html.Append("<label>Max users <input name=\"identityMaxUsers\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.IdentityMaxUsers)).Append("\"></label>");
        html.Append("<label>Max keys <input name=\"identityMaxKeys\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.IdentityMaxKeys)).Append("\"></label>");
        html.Append("</fieldset>");

        html.Append("<fieldset><legend>Models</legend>");
        for (int index = 0; index < form.Models.Length; index++)
        {
            ConfigModelForm model = form.Models[index];
            html.Append("<fieldset><legend>Model #").Append(index + 1).Append("</legend>");
            html.Append("<label>Alias <input name=\"models.alias\" required value=\"")
                .Append(Encode(model.Alias)).Append("\"></label>");
            html.Append("<label>Upstream model <input name=\"models.upstreamModel\" required value=\"")
                .Append(Encode(model.UpstreamModel)).Append("\"></label>");
            html.Append("<label>Input nanoUSD per million <input name=\"models.input\" type=\"number\" min=\"0\" required value=\"")
                .Append(Encode(model.InputUsdPerMillion)).Append("\"></label>");
            html.Append("<label>Cached input nanoUSD per million <input name=\"models.cachedInput\" type=\"number\" min=\"0\" required value=\"")
                .Append(Encode(model.CachedInputUsdPerMillion)).Append("\"></label>");
            html.Append("<label>Output nanoUSD per million <input name=\"models.output\" type=\"number\" min=\"0\" required value=\"")
                .Append(Encode(model.OutputUsdPerMillion)).Append("\"></label>");
            html.Append("<label>Max input tokens <input name=\"models.maxInput\" type=\"number\" min=\"1\" value=\"")
                .Append(Encode(model.MaxInputTokens ?? string.Empty)).Append("\"></label>");
            html.Append("<label>Max output tokens <input name=\"models.maxOutput\" type=\"number\" min=\"1\" value=\"")
                .Append(Encode(model.MaxOutputTokens ?? string.Empty)).Append("\"></label>");
            html.Append("<label><input type=\"checkbox\" name=\"models.supportsReasoning\" value=\"true\"")
                .Append(model.SupportsReasoning ? " checked" : string.Empty)
                .Append("> Supports reasoning</label>");
            html.Append("</fieldset>");
        }
        html.Append("</fieldset>");

        html.Append("<fieldset><legend>Limits</legend>");
        html.Append("<label>Max request body bytes <input name=\"maxRequestBodyBytes\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.MaxRequestBodyBytes)).Append("\"></label>");
        html.Append("<label>Max model prefix bytes <input name=\"maxModelPrefixBytes\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.MaxModelPrefixBytes)).Append("\"></label>");
        html.Append("<label>Max concurrent requests <input name=\"maxConcurrentRequests\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.MaxConcurrentRequests)).Append("\"></label>");
        html.Append("<label>Max concurrent requests per user <input name=\"maxConcurrentRequestsPerUser\" type=\"number\" min=\"1\" value=\"")
            .Append(Encode(form.MaxConcurrentRequestsPerUser ?? string.Empty)).Append("\"></label>");
        html.Append("<label>Default daily quota USD (blank = unlimited) <input name=\"dailyQuotaUsd\" type=\"number\" min=\"0\" step=\"0.000000001\" value=\"")
            .Append(Encode(form.DailyQuotaUsd ?? string.Empty)).Append("\"></label>");
        html.Append("</fieldset>");

        html.Append("<fieldset><legend>Statistics</legend>");
        html.Append("<label>Flush interval (ms) <input name=\"statisticsFlushMs\" type=\"number\" min=\"100\" required value=\"")
            .Append(Encode(form.StatisticsFlushMs)).Append("\"></label>");
        html.Append("<label>Hourly retention (hours) <input name=\"statisticsHourlyHours\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.StatisticsHourlyHours)).Append("\"></label>");
        html.Append("<label>Daily retention (days) <input name=\"statisticsDailyDays\" type=\"number\" min=\"1\" required value=\"")
            .Append(Encode(form.StatisticsDailyDays)).Append("\"></label>");
        html.Append("</fieldset>");

        html.Append("<button type=\"submit\">Save configuration</button></form>");
        AppendLogout(html, session);
        await WritePageAsync(context, "Configuration", html.ToString());
    }

    private static ConfigForm ParseConfigForm(IFormCollection form)
    {
        var parsed = new ConfigForm
        {
            ListenUrl = form["listenUrl"].ToString(),
            UpstreamBaseUrl = form["upstreamBaseUrl"].ToString(),
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
        };

        string[] aliases = form["models.alias"].ToArray()!;
        string[] upstreamModels = form["models.upstreamModel"].ToArray()!;
        string[] inputs = form["models.input"].ToArray()!;
        string[] cachedInputs = form["models.cachedInput"].ToArray()!;
        string[] outputs = form["models.output"].ToArray()!;
        string[] maxInputs = form["models.maxInput"].ToArray()!;
        string[] maxOutputs = form["models.maxOutput"].ToArray()!;
        string[] reasonings = form["models.supportsReasoning"].ToArray()!;

        int count = aliases.Length;
        var models = new ConfigModelForm[count];
        for (int index = 0; index < count; index++)
        {
            models[index] = new ConfigModelForm
            {
                Alias = Pick(aliases, index),
                UpstreamModel = Pick(upstreamModels, index),
                InputUsdPerMillion = Pick(inputs, index, "0"),
                CachedInputUsdPerMillion = Pick(cachedInputs, index, "0"),
                OutputUsdPerMillion = Pick(outputs, index, "0"),
                MaxInputTokens = NullIfBlank(Pick(maxInputs, index, string.Empty)),
                MaxOutputTokens = NullIfBlank(Pick(maxOutputs, index, string.Empty)),
                SupportsReasoning = index < reasonings.Length &&
                    string.Equals(reasonings[index], "true", StringComparison.Ordinal),
            };
        }
        parsed.Models = models;
        return parsed;
    }

    private static string Pick(string[] values, int index, string fallback = "") =>
        index < values.Length ? values[index] : fallback;

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private bool RequireAdmin(
        HttpContext context,
        out UiSession? session,
        out IdentityPrincipal? principal)
    {
        if (_sessions.TryGet(context, out session, out principal) &&
            session!.Role == IdentityRole.Admin && principal!.User.Role == IdentityRole.Admin)
            return true;

        context.Response.Redirect("/ui/login");
        return false;
    }

    private async ValueTask<PostContext?> RequireAdminPostAsync(HttpContext context)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return null;

        if (!IsSameOrigin(context))
        {
            await WriteStatusAsync(context, StatusCodes.Status403Forbidden, CrossOriginMessage);
            return null;
        }

        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (UiSessionStore.IsValidCsrf(session!, form["csrf"].ToString()))
            return new PostContext(session!, form);

        await WriteStatusAsync(context, StatusCodes.Status403Forbidden, "Invalid request token.");
        return null;
    }

    private static bool IsSameOrigin(HttpContext context) =>
        SameOriginPolicy.IsSameOrigin(
            context.Request.Scheme,
            context.Request.Host.ToString(),
            ReadSingleHeader(context, "Origin"),
            ReadSingleHeader(context, "Referer"));

    private static string? ReadSingleHeader(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out StringValues values) && values.Count == 1
            ? values[0]
            : null;

    private static long? ParseUserId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw new InvalidOperationException("Invalid user identifier.");
        return id;
    }

    private static long? ParseQuota(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal usd) || usd < 0)
            throw new InvalidOperationException("Quota must be a non-negative USD amount.");
        decimal nanoUsd = decimal.Ceiling(usd * 1_000_000_000m);
        if (nanoUsd > long.MaxValue)
            throw new InvalidOperationException("Quota is too large.");
        return (long)nanoUsd;
    }

    private static string FormatQuota(long? nanoUsd) =>
        nanoUsd is long value
            ? $"${value / 1_000_000_000m:0.#########}/day"
            : "unlimited";

    private static string FormatQuotaInput(long? nanoUsd) =>
        nanoUsd is long value
            ? (value / 1_000_000_000m).ToString("0.#########", CultureInfo.InvariantCulture)
            : string.Empty;

    private static void AppendCsrf(StringBuilder html, UiSession session) =>
        html.Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Encode(session.CsrfToken)).Append("\">");

    private static void AppendAdminNav(StringBuilder html) =>
        html.Append("<nav><a href=\"/ui/admin/users\">Users</a> <a href=\"/ui/admin/config\">Config</a></nav>");

    private static void AppendLogout(StringBuilder html, UiSession session)
    {
        html.Append("<form method=\"post\" action=\"/ui/logout\">");
        AppendCsrf(html, session);
        html.Append("<button type=\"submit\">Sign out</button></form>");
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    private static async Task WriteStatusAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await WritePageAsync(context, "Router Kely", $"<p>{Encode(message)}</p><p><a href=\"/ui\">Continue</a></p>");
    }

    private static Task WritePageAsync(HttpContext context, string title, string body)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'self'; img-src 'self' data:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        string html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light dark">
            <title>{{Encode(title)}}</title><link rel="stylesheet" href="{{StylesheetPath}}"></head><body><main>{{body}}</main></body></html>
            """;
        return context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static byte[] LoadStylesheet()
    {
        using Stream stream = typeof(AdminUiService).Assembly.GetManifestResourceStream(StylesheetResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{StylesheetResourceName}'.");
        var stylesheet = new byte[stream.Length];
        stream.ReadExactly(stylesheet);
        return stylesheet;
    }

    private sealed class LoginRateLimiter
    {
        private readonly object _gate = new();
        private readonly Dictionary<IPAddress, int> _byAddress = [];
        private DateTimeOffset _window = DateTimeOffset.UtcNow;
        private int _global;

        public bool TryAcquire(IPAddress? address)
        {
            address ??= IPAddress.None;
            lock (_gate)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - _window >= TimeSpan.FromMinutes(1))
                {
                    _window = now;
                    _global = 0;
                    _byAddress.Clear();
                }

                int perAddress = _byAddress.GetValueOrDefault(address);
                if (_global >= 60 || perAddress >= 10)
                    return false;
                _global++;
                _byAddress[address] = perAddress + 1;
                return true;
            }
        }
    }

    private sealed record PostContext(UiSession Session, IFormCollection Form);
}
