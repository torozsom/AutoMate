namespace Services.Templating;

/// <summary>
///     Centralizes template and generated-output path resolution with traversal protection.
/// </summary>
internal static class TemplatePaths
{
    /// <summary>
    ///     Directory containing embedded Scriban templates copied to the application output.
    /// </summary>
    public static readonly string TemplatesDirectory
        = Path.Combine(AppContext.BaseDirectory, "Templating", "Templates");

    /// <summary>
    ///     Full path to the template manifest file.
    /// </summary>
    public static string ManifestPath => Path.Combine(TemplatesDirectory, "template-manifest.json");

    /// <summary>
    ///     Resolves a template file path and ensures it stays under the templates directory.
    /// </summary>
    public static string ResolveTemplatePath(string templateFile)
    {
        var templatePath = Path.GetFullPath(Path.Combine(TemplatesDirectory, templateFile));
        var templatesRoot = Path.GetFullPath(TemplatesDirectory);

        if (!IsPathUnderRoot(templatePath, templatesRoot))
            throw new InvalidOperationException($"Template path escapes the templates directory: {templateFile}");

        return templatePath;
    }

    /// <summary>
    ///     Resolves an output file path and ensures it stays under the target output directory.
    /// </summary>
    public static string ResolveOutputPath(string outputDirectory, string relativePath)
    {
        var outputRoot = Path.GetFullPath(outputDirectory);
        var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));

        if (!IsPathUnderRoot(outputPath, outputRoot))
            throw new InvalidOperationException($"Generated output path escapes the target directory: {relativePath}");

        return outputPath;
    }

    /// <summary>
    ///     Normalizes manifest output paths and rejects absolute or parent-traversal paths.
    /// </summary>
    public static string NormalizeRelativeOutputPath(string outputFile)
    {
        if (string.IsNullOrWhiteSpace(outputFile))
            throw new InvalidOperationException("Template manifest contains an empty output file path.");

        if (Path.IsPathRooted(outputFile))
            throw new InvalidOperationException($"Template output path must be relative: {outputFile}");

        var normalized = outputFile.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new InvalidOperationException($"Template output path cannot contain parent traversal: {outputFile}");

        return normalized;
    }

    /// <summary>
    ///     Resolves the root used when calculating project paths for template rendering.
    /// </summary>
    public static string ResolveTemplateRoot(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || outputDirectory == ".")
            return Directory.GetCurrentDirectory();

        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        return string.Equals(Path.GetFileName(fullOutputDirectory), ".automate", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory
            : fullOutputDirectory;
    }

    /// <summary>
    ///     Checks whether a resolved path is equal to or beneath a resolved root path.
    /// </summary>
    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}