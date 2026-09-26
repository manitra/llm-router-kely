using System.Security.Cryptography;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;

namespace RouterKely.Ui;

public sealed class UiSessionStore
{
    public const string CookieName = "rk_session";
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    private const int MaximumSessions = 1_024;

    private readonly object _gate = new();
    private readonly Dictionary<string, UiSession> _sessions = new(StringComparer.Ordinal);
    private readonly ApiKeyAuthenticator _authenticator;

    public UiSessionStore(ApiKeyAuthenticator authenticator)
    {
        _authenticator = authenticator;
    }

    public UiSession Create(IdentityPrincipal principal)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = GenerateToken();
        string csrf = CreateCsrfToken(id);
        var session = new UiSession(
            id,
            principal.User.Id,
            principal.Key.Id,
            principal.User.Role,
            csrf,
            now,
            now,
            now + AbsoluteLifetime);

        lock (_gate)
        {
            Purge(now);
            if (_sessions.Count >= MaximumSessions)
            {
                UiSession oldest = _sessions.Values.MinBy(static item => item.LastSeenAt)!;
                _sessions.Remove(oldest.Id);
            }
            _sessions.Add(id, session);
        }

        return session;
    }

    public bool TryGet(HttpContext context, out UiSession? session, out IdentityPrincipal? principal)
    {
        session = null;
        principal = null;
        if (!context.Request.Cookies.TryGetValue(CookieName, out string? id) || id.Length > 128)
            return false;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out UiSession? found) ||
                found.AbsoluteExpiresAt <= now || found.LastSeenAt + IdleLifetime <= now ||
                !_authenticator.TryGetPrincipal(found.UserId, found.KeyId, out principal) ||
                principal is null || !principal.User.Enabled || !principal.Key.Enabled)
            {
                _sessions.Remove(id);
                principal = null;
                return false;
            }

            session = found with { LastSeenAt = now };
            _sessions[id] = session;
            return true;
        }
    }

    public void Destroy(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out string? id))
        {
            lock (_gate)
                _sessions.Remove(id);
        }
    }

    public static bool IsValidCsrf(UiSession session, string? token) =>
        token is not null && FixedTimeEquals(session.CsrfToken, token);

    private void Purge(DateTimeOffset now)
    {
        foreach (string id in _sessions
                     .Where(pair => pair.Value.AbsoluteExpiresAt <= now || pair.Value.LastSeenAt + IdleLifetime <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
            _sessions.Remove(id);
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string CreateCsrfToken(string sessionId)
    {
        Span<byte> secret = stackalloc byte[32];
        RandomNumberGenerator.Fill(secret);
        byte[] value = HMACSHA256.HashData(secret, System.Text.Encoding.ASCII.GetBytes(sessionId));
        CryptographicOperations.ZeroMemory(secret);
        string token = Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        CryptographicOperations.ZeroMemory(value);
        return token;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length || left.Length > 128)
            return false;

        Span<byte> leftBytes = stackalloc byte[left.Length];
        Span<byte> rightBytes = stackalloc byte[right.Length];
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] > 0x7f || right[index] > 0x7f)
                return false;
            leftBytes[index] = (byte)left[index];
            rightBytes[index] = (byte)right[index];
        }
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public sealed record UiSession(
    string Id,
    long UserId,
    long KeyId,
    IdentityRole Role,
    string CsrfToken,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt);
