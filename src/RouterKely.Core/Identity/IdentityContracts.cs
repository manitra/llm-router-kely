namespace RouterKely.Core.Identity;

public enum IdentityRole
{
    User,
    Admin
}

public sealed record IdentityUser(
    long Id,
    string Name,
    string Email,
    IdentityRole Role,
    bool Enabled,
    long? QuotaNanoUsd,
    bool IsEnvironment = false);

public sealed record IdentityKey(
    long Id,
    long UserId,
    string Name,
    string Sha256,
    string DisplayPrefix,
    string LastFour,
    bool Enabled,
    bool IsEnvironment = false)
{
    public string Masked => $"{DisplayPrefix}…{LastFour}";
}

public sealed record IdentitySnapshot(
    long Version,
    IdentityUser[] Users,
    IdentityKey[] Keys)
{
    public static IdentitySnapshot Empty { get; } = new(0, [], []);
}

public abstract record IdentityMutation;

public sealed record AddUserMutation(IdentityUser User) : IdentityMutation;

public sealed record UpdateUserMutation(IdentityUser User) : IdentityMutation;

public sealed record AddKeyMutation(IdentityKey Key) : IdentityMutation;

public sealed record RevokeKeyMutation(long KeyId) : IdentityMutation;
