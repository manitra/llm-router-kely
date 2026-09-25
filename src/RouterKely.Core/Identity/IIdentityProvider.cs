namespace RouterKely.Core.Identity;

public interface IIdentityProvider
{
    ValueTask<IdentitySnapshot> LoadAsync(CancellationToken cancellationToken);

    ValueTask<IdentitySnapshot> ApplyAsync(
        IdentityMutation mutation,
        long expectedVersion,
        CancellationToken cancellationToken);
}

