namespace EnterpriseForms.ArchitectureTests;

internal static class RepositoryPaths
{
    public static string Root { get; } = Locate();

    public static string Combine(params string[] segments) => Path.Combine([Root, .. segments]);

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found above test output directory.");
    }
}
