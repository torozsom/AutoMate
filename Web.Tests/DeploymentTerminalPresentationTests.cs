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

    /// <summary>Azure records remain separate rows across live writes and concatenated saved history.</summary>
    [Theory]
    [InlineData("azure-web", DeploymentDiagnosticStream.StandardOutput, "STDOUT")]
    [InlineData("azure-db", DeploymentDiagnosticStream.StandardError, "STDERR")]
    [InlineData("azure-system", DeploymentDiagnosticStream.System, "SYSTEM")]
    [InlineData("azure-web", DeploymentDiagnosticStream.Control, "STATUS")]
    public void Azure_records_use_source_utc_time_and_separate_rows(string channel,
        DeploymentDiagnosticStream stream, string label)
    {
        var log = new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), channel, "first",
            Stream: stream, TimestampUtc: DateTimeOffset.Parse("2026-10-11T13:08:26.123+02:00"));
        var first = DeploymentTerminalPresentation.Format(log);
        var second = DeploymentTerminalPresentation.Format(log with { Message = "second\r\n" });
        Assert.Equal($"2026-10-11 11:08:26.123 UTC [{label}] first\r\n" +
                     $"2026-10-11 11:08:26.123 UTC [{label}] second\r\n", first + second);
        Assert.Equal("first", log.Message);
    }

    /// <summary>Multiline exceptions retain indentation with one timestamped row per physical line.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Azure_multiline_messages_preserve_indentation_without_extra_trailing_rows(string newline)
    {
        var log = new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), "azure-web",
            $"error{newline}  at Example(){newline}", Stream: DeploymentDiagnosticStream.StandardError,
            TimestampUtc: DateTimeOffset.Parse("2026-10-11T11:08:26Z"));
        Assert.Equal("2026-10-11 11:08:26.000 UTC [STDERR] error\r\n" +
                     "2026-10-11 11:08:26.000 UTC [STDERR]   at Example()\r\n",
            DeploymentTerminalPresentation.Format(log));
    }

    /// <summary>Older Azure records still separate cleanly without inventing a source timestamp.</summary>
    [Theory]
    [InlineData("azure-web", "STDOUT")]
    [InlineData("azure-db", "STDOUT")]
    [InlineData("azure-system", "SYSTEM")]
    public void Legacy_azure_records_do_not_invent_timestamps(string channel, string label)
    {
        var log = new DeploymentTerminalLog(1, Guid.NewGuid(), Guid.NewGuid(), channel, "message");
        Assert.Equal($"[{label}] message\r\n", DeploymentTerminalPresentation.Format(log));
    }
}
