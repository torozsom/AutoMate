using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Docker.DotNet.Models;
using Infrastructure.Docker;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Verifies stream parsing, strict ownership and confirmed overlap without contacting Docker.</summary>
public sealed class DockerOutputTests
{
    /// <summary>Every possible byte split retains Unicode, timestamps, CRLF and an EOF partial line.</summary>
    [Fact]
    public void Utf8_frame_boundaries_do_not_change_output()
    {
        const string stamp = "2026-10-04T08:00:00.123456789Z";
        var bytes = Encoding.UTF8.GetBytes($"{stamp} árvíz 😀\r\n{stamp} \n{stamp} partial");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var decoder = new DockerLogDecoder(true, TimeProvider.System);
            var lines = decoder.Feed(bytes.AsSpan(0, split)).Concat(decoder.Feed(bytes.AsSpan(split), true)).ToArray();
            Assert.Equal(new[] { "árvíz 😀\r\n", "\n", "partial" }, lines.Select(e => e.Text));
            Assert.All(lines, e => Assert.Equal(stamp, e.ProviderTimestamp));
            Assert.All(lines, e => Assert.False(e.Omitted));
        }
    }

    /// <summary>Independent stdout/stderr decoders keep interleaved partial lines apart and retain bare CR.</summary>
    [Fact]
    public void Separate_streams_preserve_partial_lines_and_carriage_returns()
    {
        var stdout = new DockerLogDecoder(false, TimeProvider.System);
        var stderr = new DockerLogDecoder(false, TimeProvider.System);
        Assert.Empty(stdout.Feed("out"u8));
        Assert.Empty(stderr.Feed("err"u8));
        Assert.Equal("output\r\n", Assert.Single(stdout.Feed("put\r\n"u8)).Text);
        Assert.Equal(new[] { "error\r", "next" }, stderr.Feed("or\rnext"u8, true).Select(e => e.Text));
    }

    /// <summary>No prefix of an oversized secret-bearing line escapes before the explicit omission.</summary>
    [Fact]
    public void Oversized_lines_are_omitted_whole_and_next_line_recovers()
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);
        Assert.Empty(decoder.Feed(Encoding.UTF8.GetBytes("password=" + new string('s', 9000))));
        var result = decoder.Feed("\r\nok\n"u8);
        Assert.True(result[0].Omitted);
        Assert.DoesNotContain("password", result[0].Text);
        Assert.Equal("ok\n", result[1].Text);
    }

    /// <summary>Stderr progress remains informational; recognizable failures and identities gain structured attributes.</summary>
    [Theory]
    [InlineData("#12 exporting sha256:abcdef123456\r\n", "BuildProgress", "Information", "build")]
    [InlineData(" Container sample-web Started\n", "Lifecycle", "Information", "compose")]
    [InlineData("#3 ERROR [build 2/3] failed\n", "BuildProgress", "Error", "build")]
    [InlineData("failed to solve: process failed\n", "Log", "Error", "compose")]
    [InlineData("unknown progress\r", "Log", "Information", "compose")]
    [InlineData("\r\n", "Log", "Information", "compose")]
    public void Compose_preserves_text_and_interprets_recognized_progress(string text, string kind, string severity,
        string phase)
    {
        var e = DockerDiagnosticNormalizer.Compose(Guid.NewGuid(), Guid.NewGuid(), "sample", text,
            DeploymentDiagnosticStream.StandardError, 7);
        Assert.Equal(text, e.Message);
        Assert.Equal(kind, e.Kind.ToString());
        Assert.Equal(severity, e.Severity.ToString());
        Assert.Equal(phase, e.Attributes!["phase"]);
        Assert.Equal(DeploymentDiagnosticStream.StandardError, e.SourceIdentity!.Stream);
        Assert.Equal(7, e.Sequence);
        if (text.Contains("sha256")) Assert.Equal("sha256:abcdef123456", e.Attributes["image_id"]);
    }

    /// <summary>Web labels bind a deployment while stable database labels remain valid across redeployments.</summary>
    [Fact]
    public void Ownership_never_trusts_a_display_name_or_other_deployment()
    {
        var target = Target();
        var web = target.Containers[0];
        var db = target.Containers[1];
        var labels = Labels(target, web);
        Assert.True(DockerDiagnosticNormalizer.IsOwned(target, web, labels));
        Assert.False(DockerDiagnosticNormalizer.IsOwned(target with { DeploymentId = Guid.NewGuid() }, web, labels));
        Assert.False(DockerDiagnosticNormalizer.IsOwned(target with { ProjectId = Guid.NewGuid() }, web, labels));
        Assert.True(DockerDiagnosticNormalizer.IsOwned(target with { DeploymentId = Guid.NewGuid() }, db,
            Labels(target, db)));
        Assert.False(DockerDiagnosticNormalizer.IsOwned(target, web,
            new Dictionary<string, string> { ["name"] = web.Name }));
    }

    /// <summary>Pre-label Compose containers remain viewable, but can never enter the daemon lifecycle feed.</summary>
    [Fact]
    public void Legacy_compose_ownership_is_only_allowed_for_runtime_streams()
    {
        var target = Target();
        var labels = new Dictionary<string, string>
            { ["com.docker.compose.project"] = target.ComposeProject, ["com.docker.compose.service"] = "web" };
        Assert.True(DockerDiagnosticNormalizer.IsOwned(target, target.Containers[0], labels, true));
        Assert.False(DockerDiagnosticNormalizer.IsOwned(target, target.Containers[0], labels));
        Assert.Null(DockerDiagnosticNormalizer.Daemon(target, Event(labels, "start")));
        labels["io.automate.owner"] = "other";
        Assert.False(DockerDiagnosticNormalizer.IsOwned(target, target.Containers[0], labels, true));
    }

    /// <summary>Only registered actions reach Build; provider secrets are excluded rather than copied as attributes.</summary>
    [Theory]
    [InlineData("create", "Information")]
    [InlineData("start", "Information")]
    [InlineData("restart", "Information")]
    [InlineData("health_status: unhealthy", "Warning")]
    [InlineData("die", "Error")]
    [InlineData("destroy", "Information")]
    public void Daemon_lifecycle_is_owned_correlated_and_allowlisted(string action, string severity)
    {
        var target = Target();
        var labels = Labels(target, target.Containers[0]);
        labels["exitCode"] = "1";
        labels["password"] = "private-value";
        var message = Event(labels, action);
        var e = DockerDiagnosticNormalizer.Daemon(target, message)!;
        Assert.NotNull(e);
        Assert.Equal(target.DeploymentId, e.DeploymentId);
        Assert.Equal(DeploymentTerminalChannelKind.Build, e.TerminalChannel.Kind);
        Assert.Equal(severity, e.Severity.ToString());
        Assert.False(e.Attributes!.ContainsKey("password"));
        Assert.Equal(e.EventId, DockerDiagnosticNormalizer.Daemon(target, message)!.EventId);
        Assert.Null(DockerDiagnosticNormalizer.Daemon(target, Event(labels, "exec_create: echo private-value")));
        message.Type = "image";
        Assert.Null(DockerDiagnosticNormalizer.Daemon(target, message));
    }

    /// <summary>Replay skips confirmed occurrences only, retaining legitimate repeated text and failed admissions.</summary>
    [Fact]
    public void Replay_checkpoint_advances_only_on_confirmation()
    {
        var replay = new DockerLogReplayWindow();
        const string stamp = "2026-10-04T08:00:00.123456789Z";
        var time = DateTimeOffset.Parse(stamp);
        var first = replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "same")!.Value;
        Assert.Null(replay.LastTimestamp);
        replay.Confirm(first.Key, first.Occurrence, time);
        var second = replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "same")!.Value;
        Assert.Equal(2, second.Occurrence);
        replay.BeginSubscription();
        Assert.Null(replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "same"));
        var retried = replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "same")!.Value;
        Assert.Equal(second, retried);
        replay.Confirm(retried.Key, retried.Occurrence, time);
        Assert.NotNull(replay.Observe(stamp, DeploymentDiagnosticStream.StandardError, "same"));
    }

    /// <summary>Saved source metadata restores redacted occurrences only for the actual container instance.</summary>
    [Fact]
    public void Replay_restores_source_metadata_and_reports_finite_eviction()
    {
        var target = Target();
        const string stamp = "2026-10-04T08:00:00.123456789Z";
        var replay = new DockerLogReplayWindow();
        replay.Restore([
            new DeploymentTerminalLog(1, target.ProjectId, target.DeploymentId, "web", "safe",
                Stream: DeploymentDiagnosticStream.StandardOutput, TimestampUtc: DateTimeOffset.Parse(stamp),
                SourceInstanceId: "container-id", SourceCursor: stamp + "/2")
        ], "container-id");
        Assert.Null(replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "safe"));
        Assert.Null(replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "safe"));
        Assert.Equal(3, replay.Observe(stamp, DeploymentDiagnosticStream.StandardOutput, "safe")!.Value.Occurrence);
        for (var i = 0; i < 4097; i++) replay.Confirm(i.ToString(), 1, DateTimeOffset.UtcNow);
        Assert.True(replay.Evicted);
    }

    /// <summary>Creates a non-secret registered web/database inventory.</summary>
    internal static DockerDeploymentTarget Target()
    {
        return new DockerDeploymentTarget(Guid.NewGuid(), Guid.NewGuid(), "sample",
            [new DockerContainerTarget("sample-web", "web"), new DockerContainerTarget("sample-db", "db", true)],
            DateTimeOffset.UtcNow);
    }

    /// <summary>Creates canonical explicit ownership labels.</summary>
    internal static Dictionary<string, string> Labels(DockerDeploymentTarget target, DockerContainerTarget container)
    {
        return new Dictionary<string, string>
        {
            ["io.automate.owner"] = "AutoMate",
            ["io.automate.project"] = target.ProjectId.ToString("N"),
            ["io.automate.deployment"] = target.DeploymentId.ToString("N"),
            ["io.automate.service"] = container.Channel,
            ["io.automate.scope"] = container.IsDatabase ? "project" : "deployment"
        };
    }

    /// <summary>Creates a canonical timestamped Docker lifecycle observation.</summary>
    internal static Message Event(Dictionary<string, string> labels, string action)
    {
        return new Message
        {
            Type = "container",
            Action = action,
            Actor = new Actor { ID = "container-id", Attributes = labels },
            Time = 1791100800,
            TimeNano = 1791100800123456789
        };
    }
}