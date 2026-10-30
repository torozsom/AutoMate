using System.Diagnostics;

namespace Application.Diagnostics;

/// <summary>
///     Fixed deployment operation names; user inputs never become span names.
/// </summary>
public enum DeploymentOperation
{
    /// <summary>Authorized SignalR subscription.</summary>
    SignalRJoin,

    /// <summary>SignalR group removal.</summary>
    SignalRLeave,

    /// <summary>Bounded saved terminal replay.</summary>
    SignalRReplay,

    /// <summary>SignalR browser delivery.</summary>
    SignalRSend,

    /// <summary>Central diagnostic redaction.</summary>
    Redaction,

    /// <summary>Diagnostic storage confirmation.</summary>
    Persistence,

    /// <summary>Live diagnostic delivery.</summary>
    Delivery,

    /// <summary>Docker Compose command.</summary>
    DockerCompose,

    /// <summary>Docker image build.</summary>
    DockerBuild,

    /// <summary>Docker daemon lifecycle subscription.</summary>
    DockerEvents,

    /// <summary>Owned container output subscription.</summary>
    DockerLogs,

    /// <summary>One GitHub run query.</summary>
    GitHubPoll,

    /// <summary>GitHub workflow state observation.</summary>
    GitHubObserve,

    /// <summary>One Azure runtime poll.</summary>
    AzurePoll,

    /// <summary>One Azure Monitor log page.</summary>
    AzureLogs,

    /// <summary>One Azure metric query.</summary>
    AzureMetrics,

    /// <summary>GitHub job-state query.</summary>
    GitHubJobs,

    /// <summary>GitHub completed-run/job log retrieval.</summary>
    GitHubLogs,

    /// <summary>Local deployment orchestration.</summary>
    LocalDeploy,

    /// <summary>Docker CLI metric subscription.</summary>
    DockerMetrics
}

/// <summary>
///     Finite outcomes, without exception bodies or provider diagnostics.
/// </summary>
public enum DeploymentTraceOutcome
{
    /// <summary>Operation completed successfully.</summary>
    Completed,

    /// <summary>Operation failed.</summary>
    Failed,

    /// <summary>Caller canceled the operation.</summary>
    Canceled,

    /// <summary>Authorization denied the operation.</summary>
    Denied,

    /// <summary>The operation timed out.</summary>
    TimedOut
}

/// <summary>
///     Creates correlated, fixed-name spans without SDK dependencies or exception/payload
///     capture.
/// </summary>
public static class DeploymentTracing
{
    /// <summary>
    ///     Starts a child of the ambient activity with GUID-only correlation attributes.
    /// </summary>
    /// <param name="operation">The reviewed finite operation used for telemetry or auditing.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <returns>The child activity, or null when no listener requests an activity.</returns>
    public static Activity? Start(DeploymentOperation operation, Guid? projectId = null, Guid? deploymentId = null)
    {
        var name = operation switch
        {
            DeploymentOperation.SignalRJoin => "deployment.signalr.join",
            DeploymentOperation.SignalRLeave => "deployment.signalr.leave",
            DeploymentOperation.SignalRReplay => "deployment.signalr.replay",
            DeploymentOperation.SignalRSend => "deployment.signalr.send",
            DeploymentOperation.Redaction => "deployment.diagnostic.redact",
            DeploymentOperation.Persistence => "deployment.diagnostic.persist",
            DeploymentOperation.Delivery => "deployment.diagnostic.deliver",
            DeploymentOperation.DockerCompose => "docker.compose.execute",
            DeploymentOperation.DockerBuild => "docker.image.build",
            DeploymentOperation.DockerEvents => "docker.daemon.events",
            DeploymentOperation.DockerLogs => "docker.container.logs",
            DeploymentOperation.GitHubPoll => "github.workflow.query",
            DeploymentOperation.GitHubObserve => "github.workflow.observe",
            DeploymentOperation.AzurePoll => "azure.runtime.poll",
            DeploymentOperation.AzureLogs => "azure.logs.query",
            DeploymentOperation.AzureMetrics => "azure.metrics.query",
            DeploymentOperation.GitHubJobs => "github.jobs.query",
            DeploymentOperation.GitHubLogs => "github.logs.download",
            DeploymentOperation.LocalDeploy => "deployment.local.execute",
            DeploymentOperation.DockerMetrics => "docker.metrics.stream",
            _ => "deployment.unknown"
        };
        var activity = AutoMateTelemetry.Deployments.StartActivity(name);
        activity?.SetTag("deployment.source", operation switch
        {
            DeploymentOperation.DockerCompose or DeploymentOperation.DockerBuild => "DockerCompose",
            DeploymentOperation.DockerEvents => "DockerDaemon",
            DeploymentOperation.DockerLogs or DeploymentOperation.DockerMetrics => "DockerContainer",
            DeploymentOperation.GitHubPoll or DeploymentOperation.GitHubObserve or DeploymentOperation.GitHubJobs
                or DeploymentOperation.GitHubLogs => "GitHubActions",
            DeploymentOperation.AzurePoll or DeploymentOperation.AzureLogs or DeploymentOperation.AzureMetrics =>
                "AzureContainerApps",
            DeploymentOperation.SignalRJoin or DeploymentOperation.SignalRLeave or DeploymentOperation.SignalRReplay
                or DeploymentOperation.SignalRSend => "SignalR",
            _ => "AutoMate"
        });
        if (projectId.HasValue) activity?.SetTag("deployment.project.id", projectId.Value);
        if (deploymentId.HasValue) activity?.SetTag("deployment.id", deploymentId.Value);
        return activity;
    }

    /// <summary>
    ///     Records a fixed outcome; cancellation is distinct from operational failure.
    /// </summary>
    /// <param name="activity">Recorded deployment outcomes grouped by UTC day.</param>
    /// <param name="outcome">The finite recorded operation or deployment outcome.</param>
    public static void Finish(Activity? activity, DeploymentTraceOutcome outcome)
    {
        activity?.SetTag("deployment.outcome", outcome switch
        {
            DeploymentTraceOutcome.Completed => "completed",
            DeploymentTraceOutcome.Canceled => "canceled",
            DeploymentTraceOutcome.Denied => "denied",
            DeploymentTraceOutcome.TimedOut => "timed_out",
            _ => "failed"
        });
        activity?.SetStatus(outcome switch
        {
            DeploymentTraceOutcome.Completed => ActivityStatusCode.Ok,
            DeploymentTraceOutcome.Canceled => ActivityStatusCode.Unset,
            _ => ActivityStatusCode.Error
        });
    }

    /// <summary>
    ///     Traces synchronous normalization without recording inputs, results or exceptions.
    /// </summary>
    /// <param name="operation">The reviewed finite operation used for telemetry or auditing.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="action">
    ///     The operation to execute while preserving its result and exception identity.
    /// </param>
    /// <typeparam name="T">The result type returned by the supplied operation.</typeparam>
    /// <returns>The supplied operation's result, preserving its exception and cancellation semantics.</returns>
    public static T Run<T>(DeploymentOperation operation, Guid? projectId, Guid? deploymentId, Func<T> action)
    {
        using var activity = Start(operation, projectId, deploymentId);
        try
        {
            var result = action();
            Finish(activity, DeploymentTraceOutcome.Completed);
            return result;
        }
        catch (Exception)
        {
            Finish(activity, DeploymentTraceOutcome.Failed);
            throw;
        }
    }

    /// <summary>
    ///     Traces a provider/sink call and preserves its result, exception and cancellation
    ///     semantics.
    /// </summary>
    /// <param name="operation">The reviewed finite operation used for telemetry or auditing.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <param name="action">
    ///     The operation to execute while preserving its result and exception identity.
    /// </param>
    /// <param name="succeeded">
    ///     Optional result predicate used to distinguish provider success from a nonthrowing
    ///     failure.
    /// </param>
    /// <typeparam name="T">The result type returned by the supplied operation.</typeparam>
    /// <returns>
    ///     A task returning the supplied operation's result, preserving its exception and
    ///     cancellation semantics.
    /// </returns>
    /// <exception cref="OperationCanceledException">The supplied operation observes cancellation.</exception>
    public static async Task<T> RunAsync<T>(DeploymentOperation operation, Guid? projectId, Guid? deploymentId,
        CancellationToken cancellationToken, Func<Task<T>> action, Func<T, bool>? succeeded = null)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = DeploymentTraceOutcome.Completed;
        using var activity = Start(operation, projectId, deploymentId);
        try
        {
            var result = await action();
            outcome = succeeded is null || succeeded(result)
                ? DeploymentTraceOutcome.Completed
                : DeploymentTraceOutcome.Failed;
            Finish(activity, outcome);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = DeploymentTraceOutcome.Canceled;
            Finish(activity, outcome);
            throw;
        }
        catch (Exception)
        {
            outcome = DeploymentTraceOutcome.Failed;
            Finish(activity, outcome);
            throw;
        }
        finally
        {
            var source = operation switch
            {
                DeploymentOperation.Persistence or DeploymentOperation.Delivery => "AutoMate",
                DeploymentOperation.AzurePoll or DeploymentOperation.AzureLogs or DeploymentOperation.AzureMetrics =>
                    "AzureContainerApps",
                DeploymentOperation.DockerLogs or DeploymentOperation.DockerMetrics or DeploymentOperation.DockerEvents
                    => "DockerContainer",
                DeploymentOperation.GitHubPoll or DeploymentOperation.GitHubObserve or DeploymentOperation.GitHubJobs
                    or DeploymentOperation.GitHubLogs => "GitHub",
                _ => "AutoMate"
            };
            OperationalMetrics.Record(source, Enum.IsDefined(operation) ? operation.ToString() : "Unknown",
                outcome.ToString().ToLowerInvariant(), started);
        }
    }

    /// <summary>
    ///     Traces calls without results using the same outcome and cancellation policy.
    /// </summary>
    /// <param name="operation">The reviewed finite operation used for telemetry or auditing.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <param name="action">
    ///     The operation to execute while preserving its result and exception identity.
    /// </param>
    /// <returns>A task that completes after the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The supplied operation observes cancellation.</exception>
    public static Task RunAsync(DeploymentOperation operation, Guid? projectId, Guid? deploymentId,
        CancellationToken cancellationToken, Func<Task> action)
    {
        return RunAsync(operation, projectId, deploymentId,
            cancellationToken, async () =>
            {
                await action();
                return true;
            });
    }
}
