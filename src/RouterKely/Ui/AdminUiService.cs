using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Primitives;
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
    private readonly UiSessionStore _sessions;
    private readonly LoginRateLimiter _loginRateLimiter = new();

    public AdminUiService(
        ApiKeyAuthenticator authenticator,
        IdentityAdminService identities,
        UiSessionStore sessions)
    {
        _authenticator = authenticator;
        _identities = identities;
        _sessions = sessions;
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
        html.Append("<h1>Users</h1><p><a href=\"/ui/admin/users\">Users</a></p>");
        html.Append("<table><thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Quota</th></tr></thead><tbody>");
        foreach (IdentityUser user in _identities.Snapshot.Users.OrderBy(user => user.Id))
        {
            html.Append("<tr><td>");
            if (user.IsEnvironment)
                Encode(html, user.Name);
            else
                html.Append("<a href=\"/ui/admin/users/").Append(user.Id).Append("\">").Append(Encode(user.Name)).Append("</a>");
            html.Append("</td><td>").Append(Encode(user.Email))
                .Append("</td><td>").Append(user.Role == IdentityRole.Admin ? "admin" : "user")
                .Append("</td><td>").Append(user.Enabled ? "enabled" : "disabled")
                .Append("</td><td>").Append(FormatQuota(user.QuotaNanoUsd)).Append("</td></tr>");
        }
        html.Append("</tbody></table>");
        html.Append("""
            <h2>Add user</h2>
            <form method="post" action="/ui/actions/users/create">
        """);
        AppendCsrf(html, session!);
        html.Append("""
              <label>Name <input name="name" maxlength="100" required></label>
              <label>Email <input name="email" type="email" maxlength="254" required></label>
              <label>Daily quota USD <input name="quotaUsd" type="number" min="0" step="0.000000001" placeholder="unlimited"></label>
              <button type="submit">Add user</button>
            </form>
        """);
        AppendLogout(html, session!);
        await WritePageAsync(context, "Users", html.ToString());
    }

    public async Task UserAsync(HttpContext context, long id, string? message = null)
    {
        if (!RequireAdmin(context, out UiSession? session, out _))
            return;

        IdentityUser? user = _identities.Snapshot.Users.SingleOrDefault(user => user.Id == id && !user.IsEnvironment);
        if (user is null)
        {
            await WriteStatusAsync(context, StatusCodes.Status404NotFound, "User not found.");
            return;
        }

        var html = new StringBuilder(4_096);
        html.Append("<p><a href=\"/ui/admin/users\">← Users</a></p><h1>").Append(Encode(user.Name)).Append("</h1>")
            .Append("<p>").Append(Encode(user.Email)).Append(" · ").Append(user.Enabled ? "enabled" : "disabled").Append(" · quota ")
            .Append(FormatQuota(user.QuotaNanoUsd)).Append("</p>");
        if (!string.IsNullOrEmpty(message))
            html.Append("<p><strong>").Append(Encode(message)).Append("</strong></p>");
        html.Append("<form method=\"post\" action=\"/ui/actions/users/").Append(user.Id).Append(user.Enabled ? "/disable\">" : "/enable\">");
        AppendCsrf(html, session!);
        html.Append("<button type=\"submit\">").Append(user.Enabled ? "Disable user" : "Enable user").Append("</button></form>");

        html.Append("<h2>Keys</h2><table><thead><tr><th>Name</th><th>Key</th><th>Status</th><th></th></tr></thead><tbody>");
        foreach (IdentityKey key in _identities.Snapshot.Keys.Where(key => key.UserId == user.Id).OrderBy(key => key.Id))
        {
            html.Append("<tr><td>").Append(Encode(key.Name)).Append("</td><td><code>").Append(Encode(key.Masked))
                .Append("</code></td><td>").Append(key.Enabled ? "enabled" : "revoked").Append("</td><td>");
            if (key.Enabled)
            {
                html.Append("<form method=\"post\" action=\"/ui/actions/keys/").Append(key.Id).Append("/revoke\">");
                AppendCsrf(html, session!);
                html.Append("<button type=\"submit\">Revoke</button></form>");
            }
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Add key</h2><form method=\"post\" action=\"/ui/actions/users/").Append(user.Id).Append("/keys/create\">");
        AppendCsrf(html, session!);
        html.Append("<label>Name <input name=\"name\" maxlength=\"100\" required></label><button type=\"submit\">Generate key</button></form>");
        await WritePageAsync(context, user.Name, html.ToString());
    }

    public async Task CreateUserAsync(HttpContext context)
    {
        PostContext? post = await RequireAdminPostAsync(context);
        if (post is null)
            return;

        try
        {
            IFormCollection form = post.Form;
            long? quota = ParseQuota(form["quotaUsd"].ToString());
            IdentityUser user = await _identities.CreateUserAsync(
                form["name"].ToString(),
                form["email"].ToString(),
                quota,
                context.RequestAborted);
            context.Response.Redirect($"/ui/admin/users/{user.Id}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

    public Task DisableUserAsync(HttpContext context, long id) => SetUserEnabledAsync(context, id, false);

    public Task EnableUserAsync(HttpContext context, long id) => SetUserEnabledAsync(context, id, true);

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

    private async Task SetUserEnabledAsync(HttpContext context, long id, bool enabled)
    {
        if (await RequireAdminPostAsync(context) is null)
            return;
        try
        {
            await _identities.SetUserEnabledAsync(id, enabled, context.RequestAborted);
            context.Response.Redirect($"/ui/admin/users/{id}");
        }
        catch (InvalidOperationException exception)
        {
            await WriteStatusAsync(context, StatusCodes.Status400BadRequest, exception.Message);
        }
    }

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

    private static void AppendCsrf(StringBuilder html, UiSession session) =>
        html.Append("<input type=\"hidden\" name=\"csrf\" value=\"").Append(Encode(session.CsrfToken)).Append("\">");

    private static void AppendLogout(StringBuilder html, UiSession session)
    {
        html.Append("<form method=\"post\" action=\"/ui/logout\">");
        AppendCsrf(html, session);
        html.Append("<button type=\"submit\">Sign out</button></form>");
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    private static void Encode(StringBuilder target, string value) => target.Append(Encode(value));

    private static async Task WriteStatusAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await WritePageAsync(context, "Router Kely", $"<p>{Encode(message)}</p><p><a href=\"/ui\">Continue</a></p>");
    }

    private static Task WritePageAsync(HttpContext context, string title, string body)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
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
