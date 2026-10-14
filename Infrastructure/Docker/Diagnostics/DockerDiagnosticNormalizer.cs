using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Docker;
using Docker.DotNet.Models;

namespace Infrastructure.Docker.Diagnostics;

/// <summary>
///     Validates explicit ownership and normalizes daemon/Compose observations without
///     forwarding provider payloads.
/// </summary>
internal static partial class DockerDiagnosticNormalizer
{
    /// <summary>
    ///     Checks labels and the expected service rather than trusting a container's display name.
    /// </summary>
    /// <param name="target">Verified project/deployment collector inventory and ownership labels.</param>
    /// <param name="container">Verified owned Docker container and terminal identity.</param>
    /// <param name="labels">Docker resource ownership labels to verify.</param>
    /// <param name="allowLegacy">Whether explicitly supported legacy identity is accepted.</param>
    /// <returns>Whether the documented owned condition holds.</returns>
    internal static bool IsOwned(DockerDeploymentTarget target, DockerContainerTarget container,
        IDictionary<string, string>? labels, bool allowLegacy = false)
    {
        if (labels is null) return false;
        if (labels.TryGetValue("io.automate.owner", out var owner))
            return owner == "AutoMate" &&
                   labels.GetValueOrDefault("io.automate.project") == target.ProjectId.ToString("N") &&
                   labels.GetValueOrDefault("io.automate.service") == container.Channel &&
                   (container.IsDatabase
                       ? labels.GetValueOrDefault("io.automate.scope") == "project"
                       : labels.GetValueOrDefault("io.automate.scope") == "deployment" &&
                         labels.GetValueOrDefault("io.automate.deployment") == target.DeploymentId.ToString("N"));
        // Already-running pre-label containers remain viewable only when their Compose ownership is verified.
        return allowLegacy && labels.GetValueOrDefault("com.docker.compose.project") == target.ComposeProject &&
               labels.GetValueOrDefault("com.docker.compose.service") ==
               (container.IsDatabase ? container.Name : "web");
    }

    /// <summary>
    ///     Accepts only registered AutoMate container lifecycle events and a small attribute
    ///     allowlist.
    /// </summary>
    /// <param name="target">Verified project/deployment collector inventory and ownership labels.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <returns>The normalized deployment-correlated observation, or null when no value is available.</returns>
    internal static DeploymentDiagnosticEvent? Daemon(DockerDeploymentTarget target, Message message)
    {
        if (message.Type != "container" || message.Actor?.Attributes is not { } labels) return null;
        var container =
            target.Containers.FirstOrDefault(c => c.Channel == labels.GetValueOrDefault("io.automate.service"));
        if (container is null || !IsOwned(target, container, labels)) return null;
        var action = message.Action ?? message.Status;
        if (action is not ("create" or "start" or "restart" or "die" or "destroy" or "stop" or "kill" or
            "health_status: healthy" or "health_status: unhealthy" or "health_status: starting")) return null;
        if (message.Time < -62_135_596_800 || message.Time > 253_402_300_799) return null;
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Time);
        if (message.TimeNano > 0)
            timestamp = DateTimeOffset.FromUnixTimeSeconds(message.TimeNano / 1_000_000_000)
                .AddTicks(message.TimeNano % 1_000_000_000 / 100);
        var exitCode = labels.GetValueOrDefault("exitCode");
        var severity = action == "die" && int.TryParse(exitCode, out var code) && code != 0
            ? DeploymentDiagnosticSeverity.Error
            : action == "health_status: unhealthy"
                ? DeploymentDiagnosticSeverity.Warning
                : DeploymentDiagnosticSeverity.Information;
        var id = message.Actor.ID ?? message.ID;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var attributes = new Dictionary<string, string>
        { ["phase"] = "lifecycle", ["action"] = action!, ["container_id"] = id, ["service"] = container.Channel };
        if (int.TryParse(exitCode, out var exit)) attributes["exit_code"] = exit.ToString(CultureInfo.InvariantCulture);
        return new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.DockerDaemon,
            DeploymentDiagnosticKind.Lifecycle, severity, timestamp,
            $"[Docker {container.Channel}] {action}{(exitCode is not null && int.TryParse(exitCode, out _) ? $" (exit {exitCode})" : "")}\r\n",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
            Cursor: message.TimeNano.ToString(CultureInfo.InvariantCulture),
            SourceIdentity: new DeploymentDiagnosticSourceIdentity(container.IsDatabase
                    ? DeploymentDiagnosticComponent.Database
                    : DeploymentDiagnosticComponent.Web,
                DeploymentDiagnosticStream.System, id),
            EventId: Identity($"{target.DeploymentId:N}/{id}/{action}/{message.Time}/{message.TimeNano}"));
    }

    /// <summary>
    ///     Parses recognized build/container progress while preserving unknown text and stream
    ///     identity.
    /// </summary>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    /// <param name="composeProject">
    ///     The normalized Compose project identity used to verify owned Docker resources.
    /// </param>
    /// <param name="text">The diagnostic context or text being processed.</param>
    /// <param name="stream">The typed diagnostic output stream.</param>
    /// <param name="sequence">The optional provider-local sequence number.</param>
    /// <returns>The normalized deployment-correlated observation.</returns>
    internal static DeploymentDiagnosticEvent Compose(Guid project, Guid? deployment, string composeProject,
        string text, DeploymentDiagnosticStream stream, long sequence)
    {
        var attributes = new Dictionary<string, string> { ["phase"] = "compose", ["stream"] = stream.ToString() };
        var step = BuildStep().Match(text);
        var state = ContainerState().Match(text);
        if (step.Success)
        {
            attributes["phase"] = "build";
            attributes["build_step"] = step.Groups[1].Value;
        }

        if (state.Success)
        {
            attributes["container_name"] = state.Groups[1].Value;
            attributes["state"] = state.Groups[2].Value.ToLowerInvariant();
        }

        var image = ImageIdentity().Match(text);
        if (image.Success) attributes["image_id"] = image.Value;
        var failed = BuildError().IsMatch(text);
        return new DeploymentDiagnosticEvent(project, deployment, DeploymentDiagnosticSource.DockerCompose,
            step.Success ? DeploymentDiagnosticKind.BuildProgress :
            state.Success ? DeploymentDiagnosticKind.Lifecycle : DeploymentDiagnosticKind.Log,
            failed ? DeploymentDiagnosticSeverity.Error : DeploymentDiagnosticSeverity.Information,
            DateTimeOffset.UtcNow, text, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
            Sequence: sequence, SourceIdentity: new DeploymentDiagnosticSourceIdentity(step.Success
                ? DeploymentDiagnosticComponent.Build
                : DeploymentDiagnosticComponent.Compose, stream, composeProject));
    }

    /// <summary>
    ///     Creates a repeatable event identity from non-secret correlation or already-redacted
    ///     content.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The resolved internal identity.</returns>
    internal static Guid Identity(string value)
    {
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    }

    /// <summary>
    ///     Reads an optional label from the Docker SDK's mutable dictionary contract.
    /// </summary>
    /// <param name="labels">Docker resource ownership labels to verify.</param>
    /// <param name="key">Stable lookup or configuration key.</param>
    /// <returns>
    ///     The value or default derived from the validated input, or null when no value is
    ///     available.
    /// </returns>
    private static string? GetValueOrDefault(this IDictionary<string, string> labels, string key)
    {
        return labels.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    ///     Recognizes BuildKit's stable numeric step prefix.
    /// </summary>
    /// <returns>The cached pattern for this documented normalization or validation rule.</returns>
    [GeneratedRegex(@"^#(\d+)\s", RegexOptions.CultureInvariant)]
    private static partial Regex BuildStep();

    /// <summary>
    ///     Recognizes Compose container transitions while leaving unfamiliar output readable.
    /// </summary>
    /// <returns>The cached pattern for this documented normalization or validation rule.</returns>
    [GeneratedRegex(
        @"^\s*Container\s+(\S+)\s+(Creating|Created|Starting|Started|Stopping|Stopped|Removing|Removed|Running|Healthy|Waiting)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContainerState();

    /// <summary>
    ///     Recognizes a bounded content-addressed image identity.
    /// </summary>
    /// <returns>The cached pattern for this documented normalization or validation rule.</returns>
    [GeneratedRegex(@"sha256:[0-9a-fA-F]{12,64}", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIdentity();

    /// <summary>
    ///     Classifies explicit errors without treating every stderr line as a failure.
    /// </summary>
    /// <returns>The cached pattern for this documented normalization or validation rule.</returns>
    [GeneratedRegex(@"(?:^|\s)(?:ERROR:|error:|failed to solve:|fatal:|ERROR \[)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildError();
}
