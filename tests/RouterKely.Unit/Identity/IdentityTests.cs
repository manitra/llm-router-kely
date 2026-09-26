using System.Security.Cryptography;
using System.Text;
using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using Xunit;

namespace RouterKely.Unit.Identity;

public sealed class IdentityTests
{
    [Fact]
    public void GeneratedKeyStoresOnlyItsHashAndDisplayFragments()
    {
        GeneratedApiKey generated = ApiKeyGenerator.Generate(4, 3, "VS Code");

        Assert.StartsWith("sk-rk_", generated.Plaintext);
        Assert.Equal(49, generated.Plaintext.Length);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(generated.Plaintext))),
            generated.Key.Sha256);
        Assert.DoesNotContain(generated.Plaintext, generated.Key.ToString());
    }

    [Fact]
    public void AuthenticatorPublishesAndRevokesFileBackedKeys()
    {
        GeneratedApiKey generated = ApiKeyGenerator.Generate(2, 2, "Developer");
        var user = new IdentityUser(2, "Developer", "developer@example.com", IdentityRole.User, true, null);
        var authenticator = new ApiKeyAuthenticator(
            "sk-rk-environment-admin",
            new IdentityUser(1, "Admin", "admin@example.com", IdentityRole.Admin, true, null, true),
            1,
            "Environment",
            new IdentitySnapshot(1, [user], [generated.Key]));

        Assert.True(authenticator.TryAuthenticateToken(generated.Plaintext, out IdentityPrincipal? principal));
        Assert.Equal(2, principal!.User.Id);

        authenticator.Update(new IdentitySnapshot(2, [user], [generated.Key with { Enabled = false }]));

        Assert.False(authenticator.TryAuthenticateToken(generated.Plaintext, out _));
        Assert.True(authenticator.TryAuthenticateToken("sk-rk-environment-admin", out _));
    }

    [Fact]
    public async Task FileProviderPersistsVersionedMutationsWithoutPlaintextKeys()
    {
        string directory = Path.Combine(FindRepositoryRoot(), "scripts", $".tmp-identity-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "identities.json");
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new FileIdentityProvider(path, 10, 20, 1, 1);
            IdentitySnapshot empty = await provider.LoadAsync(CancellationToken.None);
            var user = new IdentityUser(2, "Developer", "developer@example.com", IdentityRole.User, true, 1_000);
            IdentitySnapshot withUser = await provider.ApplyAsync(
                new AddUserMutation(user),
                empty.Version,
                CancellationToken.None);
            GeneratedApiKey generated = ApiKeyGenerator.Generate(2, 2, "VS Code");
            IdentitySnapshot withKey = await provider.ApplyAsync(
                new AddKeyMutation(generated.Key),
                withUser.Version,
                CancellationToken.None);

            string json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(generated.Plaintext, json);
            Assert.Contains(generated.Key.Sha256, json);

            var reloadedProvider = new FileIdentityProvider(path, 10, 20, 1, 1);
            IdentitySnapshot reloaded = await reloadedProvider.LoadAsync(CancellationToken.None);
            Assert.Equal(withKey.Version, reloaded.Version);
            Assert.Equal(withKey.Users, reloaded.Users);
            Assert.Equal(withKey.Keys, reloaded.Keys);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RouterKely.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
