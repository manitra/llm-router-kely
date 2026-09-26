namespace RouterKely.Compatibility;

public sealed record CompatibilityIdentity(
    long UserId,
    string UserName,
    string UserEmail,
    long KeyId,
    string KeyName,
    string MaskedKey);

