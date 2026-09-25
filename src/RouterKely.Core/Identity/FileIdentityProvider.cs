namespace RouterKely.Core.Identity;

public sealed class FileIdentityProvider : IIdentityProvider
{
    public ValueTask<IdentitySnapshot> LoadAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public ValueTask<IdentitySnapshot> ApplyAsync(
        IdentityMutation mutation,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

