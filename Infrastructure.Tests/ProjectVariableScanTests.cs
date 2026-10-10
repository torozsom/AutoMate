using Infrastructure.Scanner;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests;

/// <summary>Verifies filesystem failures remain distinguishable from successfully scanned empty configuration.</summary>
public sealed class ProjectVariableScanTests
{
    /// <summary>A missing directory reaches the form's safe error guidance instead of reporting no variables.</summary>
    [Fact]
    public async Task Missing_project_directory_is_reported_as_scan_failure()
    {
        var scanner = new ProjectScannerService(NullLogger<ProjectScannerService>.Instance);
        var path = Path.Combine(Path.GetTempPath(), "automate-missing-" + Guid.NewGuid().ToString("N"), "Web.csproj");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scanner.ExtractEnvironmentVariablesAsync(path));
    }
}