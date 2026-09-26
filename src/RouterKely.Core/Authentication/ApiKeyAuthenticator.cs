using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using RouterKely.Core.Identity;

namespace RouterKely.Core.Authentication;

public sealed class ApiKeyAuthenticator
{
    private readonly IdentityUser _environmentUser;
    private readonly IdentityKey _environmentKey;
    private RuntimeSnapshot _runtime;

    public ApiKeyAuthenticator(string apiKey)
        : this(
            apiKey,
            new IdentityUser(1, "Local Administrator", "admin@localhost", IdentityRole.Admin, true, null, true),
            1,
            "Environment administrator",
            IdentitySnapshot.Empty)
    {
    }

    public ApiKeyAuthenticator(
        string apiKey,
        IdentityUser environmentUser,
        long environmentKeyId,
        string environmentKeyName,
        IdentitySnapshot fileSnapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _environmentUser = environmentUser with { IsEnvironment = true };
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(apiKey));
        _environmentKey = new IdentityKey(
            environmentKeyId,
            environmentUser.Id,
            environmentKeyName,
            Convert.ToHexStringLower(hash),
            apiKey[..Math.Min(11, apiKey.Length)],
            apiKey.Length >= 4 ? apiKey[^4..] : apiKey,
            true,
            true);
        CryptographicOperations.ZeroMemory(hash);
        _runtime = Build(fileSnapshot);
    }

    public IdentitySnapshot Snapshot => Volatile.Read(ref _runtime).Snapshot;

    public bool Authenticate(ReadOnlySpan<char> authorization) =>
        TryAuthenticate(authorization, out _);

    public bool TryAuthenticate(ReadOnlySpan<char> authorization, out IdentityPrincipal? principal)
    {
        const string prefix = "Bearer ";

        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            principal = null;
            return false;
        }

        return TryAuthenticateToken(authorization[prefix.Length..], out principal);
    }

    public bool TryAuthenticateToken(ReadOnlySpan<char> token, out IdentityPrincipal? principal)
    {
        if (token.IsEmpty || token.Length > 128)
        {
            principal = null;
            return false;
        }

        Span<byte> bytes = stackalloc byte[token.Length];
        for (int index = 0; index < token.Length; index++)
        {
            char character = token[index];
            if (character > 0x7f)
            {
                principal = null;
                return false;
            }

            bytes[index] = (byte)character;
        }

        Span<byte> actualHash = stackalloc byte[32];
        SHA256.HashData(bytes, actualHash);
        CryptographicOperations.ZeroMemory(bytes);
        KeyHash key = KeyHash.FromBytes(actualHash);
        CryptographicOperations.ZeroMemory(actualHash);
        return Volatile.Read(ref _runtime).ByHash.TryGetValue(key, out principal);
    }

    public bool TryGetPrincipal(long userId, long keyId, out IdentityPrincipal? principal)
    {
        if (Volatile.Read(ref _runtime).ByKeyId.TryGetValue(keyId, out principal) &&
            principal.User.Id == userId)
            return true;

        principal = null;
        return false;
    }

    public void Update(IdentitySnapshot fileSnapshot) =>
        Volatile.Write(ref _runtime, Build(fileSnapshot));

    private RuntimeSnapshot Build(IdentitySnapshot fileSnapshot)
    {
        IdentityUser[] users = [_environmentUser, .. fileSnapshot.Users];
        IdentityKey[] keys = [_environmentKey, .. fileSnapshot.Keys];
        var usersById = users.ToDictionary(user => user.Id);
        var principals = new List<IdentityPrincipal>(keys.Length);
        foreach (IdentityKey key in keys)
        {
            if (!key.Enabled || !usersById.TryGetValue(key.UserId, out IdentityUser? user) || !user.Enabled)
                continue;

            principals.Add(new IdentityPrincipal(user, key));
        }

        return new RuntimeSnapshot(
            new IdentitySnapshot(fileSnapshot.Version, users, keys),
            principals.ToFrozenDictionary(
                principal => KeyHash.FromHex(principal.Key.Sha256),
                principal => principal,
                KeyHashComparer.Instance),
            principals.ToFrozenDictionary(principal => principal.Key.Id));
    }

    private sealed record RuntimeSnapshot(
        IdentitySnapshot Snapshot,
        FrozenDictionary<KeyHash, IdentityPrincipal> ByHash,
        FrozenDictionary<long, IdentityPrincipal> ByKeyId);

    private readonly record struct KeyHash(ulong A, ulong B, ulong C, ulong D)
    {
        public static KeyHash FromHex(string hex) => FromBytes(Convert.FromHexString(hex));

        public static KeyHash FromBytes(ReadOnlySpan<byte> bytes) => new(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
    }

    private sealed class KeyHashComparer : IEqualityComparer<KeyHash>
    {
        public static KeyHashComparer Instance { get; } = new();

        public bool Equals(KeyHash left, KeyHash right) =>
            ((left.A ^ right.A) | (left.B ^ right.B) | (left.C ^ right.C) | (left.D ^ right.D)) == 0;

        public int GetHashCode(KeyHash value) => HashCode.Combine(value.A, value.B, value.C, value.D);
    }
}

public sealed record IdentityPrincipal(IdentityUser User, IdentityKey Key);
