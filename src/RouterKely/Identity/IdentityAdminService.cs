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

    public async ValueTask<IdentityUser> SaveUserAsync(
        long? userId,
        string name,
        string email,
        long? quotaNanoUsd,
        bool enabled,
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
            IdentityUser? existing = userId is null
                ? null
                : _fileSnapshot.Users.SingleOrDefault(user => user.Id == userId)
                    ?? throw new InvalidOperationException("User not found or cannot be changed.");

            if (Snapshot.Users.Any(user => user.Id != existing?.Id &&
                    user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A user with that email already exists.");

            if (existing is { Enabled: true, Role: IdentityRole.Admin } && !enabled &&
                Snapshot.Users.Count(candidate =>
                    candidate.Enabled && candidate.Role == IdentityRole.Admin && candidate.Id != existing.Id) == 0)
                throw new InvalidOperationException("The last enabled administrator cannot be disabled.");

            IdentityUser user = existing is null
                ? new IdentityUser(Snapshot.Users.Max(static user => user.Id) + 1, name, email, IdentityRole.User, enabled, quotaNanoUsd)
                : existing with { Name = name, Email = email, QuotaNanoUsd = quotaNanoUsd, Enabled = enabled };

            await ApplyAsync(
                existing is null ? new AddUserMutation(user) : new UpdateUserMutation(user),
                cancellationToken);
            _logger.LogInformation("Administrator saved user {UserId}", user.Id);
            return user;
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
