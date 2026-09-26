using RouterKely.Core.Authentication;
using RouterKely.Core.Identity;
using RouterKely.Core.Statistics;

namespace RouterKely.Identity;

public sealed class IdentityAdminService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IIdentityProvider _provider;
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly UsageAccumulator _usage;
    private readonly ILogger _logger;
    private IdentitySnapshot _fileSnapshot;

    public IdentityAdminService(
        IIdentityProvider provider,
        IdentitySnapshot fileSnapshot,
        ApiKeyAuthenticator authenticator,
        UsageAccumulator usage,
        ILogger logger)
    {
        _provider = provider;
        _fileSnapshot = fileSnapshot;
        _authenticator = authenticator;
        _usage = usage;
        _logger = logger;
    }

    public IdentitySnapshot Snapshot => _authenticator.Snapshot;

    public async ValueTask<IdentityUser> CreateUserAsync(
        string name,
        string email,
        long? quotaNanoUsd,
        CancellationToken cancellationToken)
    {
        name = name.Trim();
        email = email.Trim();
        if (name.Length is < 1 or > 100 || email.Length is < 3 or > 254 || !email.Contains('@'))
            throw new InvalidOperationException("Enter a valid name and email address.");
        if (quotaNanoUsd < 0)
            throw new InvalidOperationException("Quota cannot be negative.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Snapshot.Users.Any(user => user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A user with that email already exists.");

            long id = Snapshot.Users.Max(static user => user.Id) + 1;
            var user = new IdentityUser(id, name, email, IdentityRole.User, true, quotaNanoUsd);
            await ApplyAsync(new AddUserMutation(user), cancellationToken);
            _logger.LogInformation("Administrator created user {UserId}", id);
            return user;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetUserEnabledAsync(
        long userId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IdentityUser user = _fileSnapshot.Users.SingleOrDefault(user => user.Id == userId)
                ?? throw new InvalidOperationException("User not found or cannot be changed.");
            if (!enabled && user.Role == IdentityRole.Admin &&
                Snapshot.Users.Count(candidate => candidate.Enabled && candidate.Role == IdentityRole.Admin) <= 1)
                throw new InvalidOperationException("The last enabled administrator cannot be disabled.");

            await ApplyAsync(new SetUserEnabledMutation(userId, enabled), cancellationToken);
            _logger.LogInformation("Administrator set user {UserId} enabled={Enabled}", userId, enabled);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GeneratedApiKey> CreateKeyAsync(
        long userId,
        string name,
        CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100)
            throw new InvalidOperationException("Enter a key name.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            IdentityUser user = _fileSnapshot.Users.SingleOrDefault(user => user.Id == userId)
                ?? throw new InvalidOperationException("User not found or cannot receive file-backed keys.");
            if (!user.Enabled)
                throw new InvalidOperationException("Enable the user before creating a key.");

            long id = Snapshot.Keys.Max(static key => key.Id) + 1;
            GeneratedApiKey generated = ApiKeyGenerator.Generate(id, userId, name);
            await ApplyAsync(new AddKeyMutation(generated.Key), cancellationToken);
            _logger.LogInformation("Administrator created key {KeyId} for user {UserId}", id, userId);
            return generated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RevokeKeyAsync(long keyId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IdentityKey key = _fileSnapshot.Keys.SingleOrDefault(key => key.Id == keyId)
                ?? throw new InvalidOperationException("Key not found or cannot be revoked.");
            if (!key.Enabled)
                return;

            await ApplyAsync(new RevokeKeyMutation(keyId), cancellationToken);
            _logger.LogInformation("Administrator revoked key {KeyId} for user {UserId}", keyId, key.UserId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ApplyAsync(IdentityMutation mutation, CancellationToken cancellationToken)
    {
        _fileSnapshot = await _provider.ApplyAsync(
            mutation,
            _fileSnapshot.Version,
            cancellationToken);
        _authenticator.Update(_fileSnapshot);
        _usage.UpdateIdentities(_authenticator.Snapshot);
    }
}
