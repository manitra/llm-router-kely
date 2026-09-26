using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouterKely.Core.Identity;

public sealed class FileIdentityProvider : IIdentityProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly int _maxUsers;
    private readonly int _maxKeys;
    private readonly long _reservedUserId;
    private readonly long _reservedKeyId;
    private IdentitySnapshot _current = IdentitySnapshot.Empty;

    public FileIdentityProvider(
        string path,
        int maxUsers,
        int maxKeys,
        long reservedUserId,
        long reservedKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _maxUsers = maxUsers;
        _maxKeys = maxKeys;
        _reservedUserId = reservedUserId;
        _reservedKeyId = reservedKeyId;
    }

    public async ValueTask<IdentitySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _current = await ReadAsync(cancellationToken);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IdentitySnapshot> ApplyAsync(
        IdentityMutation mutation,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current.Version != expectedVersion)
                throw new InvalidOperationException("The identity snapshot changed. Reload and retry.");

            IdentitySnapshot updated = Apply(_current, mutation);
            Validate(updated);
            await WriteAsync(updated, cancellationToken);
            _current = updated;
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IdentitySnapshot Apply(IdentitySnapshot snapshot, IdentityMutation mutation) => mutation switch
    {
        AddUserMutation add => snapshot with
        {
            Version = snapshot.Version + 1,
            Users = [.. snapshot.Users, add.User]
        },
        UpdateUserMutation update => snapshot.Users.Any(user => user.Id == update.User.Id)
            ? snapshot with
            {
                Version = snapshot.Version + 1,
                Users = snapshot.Users
                    .Select(user => user.Id == update.User.Id ? update.User : user)
                    .ToArray()
            }
            : throw new InvalidOperationException("User not found."),
        AddKeyMutation add => snapshot with
        {
            Version = snapshot.Version + 1,
            Keys = [.. snapshot.Keys, add.Key]
        },
        RevokeKeyMutation revoke => snapshot with
        {
            Version = snapshot.Version + 1,
            Keys = snapshot.Keys
                .Select(key => key.Id == revoke.KeyId ? key with { Enabled = false } : key)
                .ToArray()
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mutation))
    };

    private async ValueTask<IdentitySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return IdentitySnapshot.Empty;

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        IdentityDocument document = await JsonSerializer.DeserializeAsync(
            stream,
            IdentityJsonContext.Default.IdentityDocument,
            cancellationToken)
            ?? throw new InvalidOperationException("The identity file is empty.");
        if (document.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported identity schema version {document.SchemaVersion}.");

        var snapshot = new IdentitySnapshot(
            document.Version,
            document.Users.Select(static user => new IdentityUser(
                user.Id,
                user.Name,
                user.Email,
                ParseRole(user.Role),
                user.Enabled,
                user.QuotaNanoUsd)).ToArray(),
            document.Keys.Select(static key => new IdentityKey(
                key.Id,
                key.UserId,
                key.Name,
                key.Sha256,
                key.DisplayPrefix,
                key.LastFour,
                key.Enabled)).ToArray());
        Validate(snapshot);
        return snapshot;
    }

    private async ValueTask WriteAsync(IdentitySnapshot snapshot, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Identity file requires a parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        var document = new IdentityDocument
        {
            SchemaVersion = 1,
            Version = snapshot.Version,
            Users = snapshot.Users.Select(static user => new IdentityUserDocument
            {
                Id = user.Id,
                Name = user.Name,
                Email = user.Email,
                Role = user.Role == IdentityRole.Admin ? "admin" : "user",
                Enabled = user.Enabled,
                QuotaNanoUsd = user.QuotaNanoUsd
            }).ToArray(),
            Keys = snapshot.Keys.Select(static key => new IdentityKeyDocument
            {
                Id = key.Id,
                UserId = key.UserId,
                Name = key.Name,
                Sha256 = key.Sha256,
                DisplayPrefix = key.DisplayPrefix,
                LastFour = key.LastFour,
                Enabled = key.Enabled
            }).ToArray()
        };

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    IdentityJsonContext.Default.IdentityDocument,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void Validate(IdentitySnapshot snapshot)
    {
        if (snapshot.Version < 0)
            throw new InvalidOperationException("Identity version cannot be negative.");
        if (snapshot.Users.Length > _maxUsers || snapshot.Keys.Length > _maxKeys)
            throw new InvalidOperationException("Identity limits exceeded.");
        if (snapshot.Users.Any(user => user.Id <= 0 || user.Id == _reservedUserId))
            throw new InvalidOperationException("Identity file contains an invalid or reserved user ID.");
        if (snapshot.Keys.Any(key => key.Id <= 0 || key.Id == _reservedKeyId))
            throw new InvalidOperationException("Identity file contains an invalid or reserved key ID.");
        if (snapshot.Users.Select(user => user.Id).Distinct().Count() != snapshot.Users.Length)
            throw new InvalidOperationException("Duplicate user ID.");
        if (snapshot.Users.Select(user => user.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Users.Length)
            throw new InvalidOperationException("Duplicate user email.");
        if (snapshot.Keys.Select(key => key.Id).Distinct().Count() != snapshot.Keys.Length)
            throw new InvalidOperationException("Duplicate key ID.");
        if (snapshot.Keys.Select(key => key.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Keys.Length)
            throw new InvalidOperationException("Duplicate key hash.");

        HashSet<long> userIds = snapshot.Users.Select(user => user.Id).ToHashSet();
        foreach (IdentityUser user in snapshot.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Name) || user.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(user.Email) || user.Email.Length > 254 || !user.Email.Contains('@') ||
                user.QuotaNanoUsd < 0)
                throw new InvalidOperationException("Invalid user data.");
        }

        foreach (IdentityKey key in snapshot.Keys)
        {
            if (!userIds.Contains(key.UserId) || string.IsNullOrWhiteSpace(key.Name) || key.Name.Length > 100 ||
                key.Sha256.Length != 64 || !key.Sha256.All(Uri.IsHexDigit) ||
                !key.Sha256.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                string.IsNullOrWhiteSpace(key.DisplayPrefix) || key.DisplayPrefix.Length > 20 ||
                key.LastFour.Length != 4)
                throw new InvalidOperationException("Invalid key data.");
        }
    }

    private static IdentityRole ParseRole(string role) => role switch
    {
        "user" => IdentityRole.User,
        "admin" => IdentityRole.Admin,
        _ => throw new InvalidOperationException($"Unknown identity role '{role}'.")
    };
}

internal sealed class IdentityDocument
{
    public int SchemaVersion { get; set; }
    public long Version { get; set; }
    public IdentityUserDocument[] Users { get; set; } = [];
    public IdentityKeyDocument[] Keys { get; set; } = [];
}

internal sealed class IdentityUserDocument
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public long? QuotaNanoUsd { get; set; }
}

internal sealed class IdentityKeyDocument
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string DisplayPrefix { get; set; } = string.Empty;
    public string LastFour { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(IdentityDocument))]
internal sealed partial class IdentityJsonContext : JsonSerializerContext;
