using System.Security.Cryptography;
using System.Text;

namespace RouterKely.Core.Identity;

public static class ApiKeyGenerator
{
    public static GeneratedApiKey Generate(long id, long userId, string name)
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        string encoded = Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string plaintext = $"sk-rk_{encoded}";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(plaintext)));
        var key = new IdentityKey(
            id,
            userId,
            name,
            hash,
            plaintext[..Math.Min(11, plaintext.Length)],
            plaintext[^4..],
            true);
        CryptographicOperations.ZeroMemory(random);
        return new GeneratedApiKey(plaintext, key);
    }
}

public sealed record GeneratedApiKey(string Plaintext, IdentityKey Key);
