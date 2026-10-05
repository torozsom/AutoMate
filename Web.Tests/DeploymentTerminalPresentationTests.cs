using Application.Abstractions.Diagnostics;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Checks equal live/history text treatment without relying on terminal colors.</summary>
public sealed class DeploymentTerminalPresentationTests
{
    /// <summary>Additive source metadata distinguishes stderr while retaining legacy payload compatibility.</summary>
    [Fact]
    public void Stderr_is_visible_and_legacy_stdout_text_remains_unchanged()
    {
        var log = new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), "build", "progress\r\n");
        Assert.Equal("progress\r\n", DeploymentTerminalPresentation.Format(log));
        Assert.Equal("progress\r\n",
            DeploymentTerminalPresentation.Format(log with { Stream = DeploymentDiagnosticStream.StandardOutput }));
        Assert.Equal("[stderr] progress\r\n",
            DeploymentTerminalPresentation.Format(log with { Stream = DeploymentDiagnosticStream.StandardError }));
    }
}