using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.Diagnostics;
using Infrastructure.Observability;
using OpenTelemetry;

namespace Web.Observability;

/// <summary>Sanitizes before simple/batch exporters; spans with immutable untrusted events/links fail closed.</summary>
public sealed class SafeTraceProcessor(PlatformTelemetryPolicy policy) : BaseProcessor<Activity>
{
    /// <summary>Finite omission reasons, without identifiers or payloads.</summary>
    private static readonly Counter<long> Omitted = AutoMateTelemetry.DeploymentMeter.CreateCounter<long>(
        "automate.platform.spans.omitted");

    /// <summary>Fixed deployment operation names approved for export.</summary>
    private static readonly HashSet<string> DeploymentNames = new(StringComparer.Ordinal)
    {
        "deployment.diagnostic.ingest", "deployment.diagnostic.dispatch", "deployment.diagnostic.redact",
        "deployment.diagnostic.persist", "deployment.diagnostic.deliver", "deployment.signalr.join",
        "deployment.signalr.leave", "deployment.signalr.replay", "deployment.signalr.send", "deployment.local.execute",
        "docker.compose.execute", "docker.image.build", "docker.daemon.events", "docker.container.logs",
        "docker.metrics.stream", "github.workflow.query", "github.workflow.observe", "github.jobs.query",
        "github.logs.download", "azure.runtime.poll", "azure.logs.query", "azure.metrics.query"
    };

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        if (activity.Events.Any() || activity.Links.Any())
        {
            Omit(activity, "events_or_links");
            return;
        }

        var tags = activity.TagObjects.Take(129).ToArray();
        if (tags.Length > 128)
        {
            Omit(activity, "attribute_limit");
            return;
        }

        foreach (var tag in tags) activity.SetTag(tag.Key, policy.Field(tag.Key, tag.Value));
        foreach (var baggage in activity.Baggage.ToArray()) activity.SetBaggage(baggage.Key, null);
        activity.TraceStateString = string.Empty;
        activity.SetStatus(activity.Status);
        activity.DisplayName = activity.Source.Name switch
        {
            "AutoMate.Deployments" when DeploymentNames.Contains(activity.OperationName) => activity.OperationName,
            "AutoMate.Deployments" => "deployment.operation",
            "AutoMate.Security" when activity.OperationName == "security.rate_limit.rejected" =>
                "security.rate_limit.rejected",
            "AutoMate.Security" => "security.operation",
            "AutoMate.Analysis" when activity.OperationName is "analysis.claim" or "analysis.process" or
                "analysis.context" or "analysis.provider" or "analysis.renew" or "analysis.release" or
                "analysis.publish" or "analysis.retry" => activity.OperationName,
            "AutoMate.Analysis" => "analysis.operation",
            "Microsoft.AspNetCore.SignalR.Server" => "signalr.invocation",
            _ when activity.Kind == ActivityKind.Server => "http.server.request",
            _ when activity.Kind == ActivityKind.Client => "provider.request",
            _ => "platform.operation"
        };
    }

    /// <summary>Both SDK export processors honor the Recorded flag; no payload is sent for an omitted span.</summary>
    private static void Omit(Activity activity, string reason)
    {
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        Omitted.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }
}