using Application.Abstractions.Diagnostics;

namespace Web.Components.Shared;

/// <summary>Keeps stderr identifiable in both live and saved terminal output without relying on color.</summary>
public static class DeploymentTerminalPresentation
{
    /// <summary>Preserves legacy/stdout text and prefixes stderr while retaining its original line endings.</summary>
    public static string Format(DeploymentTerminalLog log)
    {
        return log.Stream == DeploymentDiagnosticStream.StandardError ? "[stderr] " + log.Message : log.Message;
    }
}