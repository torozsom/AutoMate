using System.Xml.Linq;
using Core.DTO;

namespace Services.Scanner;

/// <summary>
///     Reads deployment-relevant metadata from SDK-style C# project files.
/// </summary>
internal static class CsprojMetadataReader
{
    /// <summary>
    ///     Fallback target framework used when a project file does not declare one.
    /// </summary>
    private const string DefaultTargetFramework = "net10.0";

    /// <summary>
    ///     Fallback .NET version used when the target framework is not a net* TFM.
    /// </summary>
    private const string DefaultDotNetVersion = "10.0";

    /// <summary>
    ///     SDK marker used by ASP.NET Core web project files.
    /// </summary>
    private const string WebSdkName = "Microsoft.NET.Sdk.Web";

    /// <summary>
    ///     Parses one .csproj file and extracts target framework, packages, references, and web SDK metadata.
    /// </summary>
    public static async Task<ProjectMetadataDto> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var targetFramework = GetTargetFramework(document);
        var dotNetVersion = GetDotNetVersion(targetFramework);

        return new ProjectMetadataDto
        {
            TargetFramework = targetFramework,
            DotNetVersion = dotNetVersion,
            IsWebProject = IsWebProject(document),
            UserSecretsId = document.Descendants("UserSecretsId").FirstOrDefault()?.Value,
            PackageReferences = GetPackageReferences(document),
            ProjectReferences = GetProjectReferences(document)
        };
    }

    /// <summary>
    ///     Reads the first target framework from TargetFramework or TargetFrameworks.
    /// </summary>
    private static string GetTargetFramework(XContainer document)
    {
        var targetFramework = document.Descendants("TargetFramework").FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(targetFramework))
            targetFramework = document.Descendants("TargetFrameworks").FirstOrDefault()?.Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

        return string.IsNullOrEmpty(targetFramework) ? DefaultTargetFramework : targetFramework;
    }

    /// <summary>
    ///     Derives the .NET version from a target framework moniker.
    /// </summary>
    private static string GetDotNetVersion(string targetFramework)
    {
        return targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..]
            : DefaultDotNetVersion;
    }

    /// <summary>
    ///     Detects ASP.NET Core web projects by inspecting the root SDK attribute.
    /// </summary>
    private static bool IsWebProject(XDocument document)
    {
        var sdkAttribute = document.Root?.Attribute("Sdk")?.Value;
        return sdkAttribute != null &&
               sdkAttribute.Contains(WebSdkName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads package references and versions from PackageReference elements.
    /// </summary>
    private static Dictionary<string, string> GetPackageReferences(XContainer document)
    {
        var packageReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageReference in document.Descendants("PackageReference"))
        {
            var include = packageReference.Attribute("Include")?.Value;
            var version = packageReference.Attribute("Version")?.Value ?? packageReference.Element("Version")?.Value;
            if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                packageReferences[include] = version;
        }

        return packageReferences;
    }

    /// <summary>
    ///     Reads project reference paths from ProjectReference elements.
    /// </summary>
    private static List<string> GetProjectReferences(XContainer document)
    {
        var projectReferences = new List<string>();
        foreach (var projectReference in document.Descendants("ProjectReference"))
        {
            var include = projectReference.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(include))
                projectReferences.Add(include.Replace('\\', '/'));
        }

        return projectReferences;
    }
}