namespace RouterKely.Unit;

/// <summary>
/// File-system locations for tests. Temporary directories live under the repository's gitignored
/// <c>scripts/.tmp*</c> tree, which the repository rules require for scratch data.
/// </summary>
internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string CreateTempDirectory(string prefix) =>
        Directory.CreateDirectory(
            Path.Combine(RepositoryRoot, "scripts", $".tmp-{prefix}-{Guid.NewGuid():N}")).FullName;

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
