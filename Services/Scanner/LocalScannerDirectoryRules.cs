namespace Services.Scanner;

/// <summary>
///     Centralizes filesystem rules used by local project discovery.
/// </summary>
internal static class LocalScannerDirectoryRules
{
    /// <summary>
    ///     Directory names skipped during recursive local repository scans.
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "testresults", ".vs", ".idea"
    };

    /// <summary>
    ///     Checks whether a directory should be skipped while scanning.
    /// </summary>
    public static bool IsExcludedDirectory(DirectoryInfo dirInfo)
    {
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return true;

        var name = dirInfo.Name;
        if (name.StartsWith('.') && !name.Equals(".git", StringComparison.OrdinalIgnoreCase))
            return true;

        return IgnoredDirectories.Contains(name);
    }

    /// <summary>
    ///     Checks whether the directory contains a Git metadata directory.
    /// </summary>
    public static bool ContainsGitDirectory(IEnumerable<string> directories)
    {
        return directories.Any(directory =>
            Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Checks whether a directory is a solution root by Git or solution file markers.
    /// </summary>
    public static bool IsSolutionRoot(DirectoryInfo directory)
    {
        return Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
               directory.EnumerateFiles("*.sln").Any() ||
               directory.EnumerateFiles("*.slnx").Any();
    }

    /// <summary>
    ///     Checks whether the repository root has a top-level solution file.
    /// </summary>
    public static bool HasSolutionFile(string repositoryPath)
    {
        return Directory.EnumerateFiles(repositoryPath, "*", SearchOption.TopDirectoryOnly).Any(file =>
            Path.GetExtension(file).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file).Equals(".slnx", StringComparison.OrdinalIgnoreCase));
    }
}