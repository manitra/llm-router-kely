using System.Security.Cryptography;
using System.Text;

namespace RouterKely.Core.Authentication;

public sealed class ApiKeyAuthenticator
{
    private readonly byte[] _expectedHash;

    public ApiKeyAuthenticator(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(apiKey));
    }

    public bool Authenticate(ReadOnlySpan<char> authorization)
    {
        const string prefix = "Bearer ";

        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> token = authorization[prefix.Length..];
        if (token.IsEmpty || token.Length > 128)
            return false;

        Span<byte> bytes = stackalloc byte[token.Length];
        for (int index = 0; index < token.Length; index++)
        {
            char character = token[index];
            if (character > 0x7f)
                return false;

            bytes[index] = (byte)character;
        }

        Span<byte> actualHash = stackalloc byte[32];
        SHA256.HashData(bytes, actualHash);
        CryptographicOperations.ZeroMemory(bytes);

        bool matches = CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash);
        CryptographicOperations.ZeroMemory(actualHash);
        return matches;
    }
}

