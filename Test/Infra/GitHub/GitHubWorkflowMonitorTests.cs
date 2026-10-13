using System.Collections.Concurrent;
using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.GitHub;
using Application.Orchestration;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using FluentAssertions;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Infra.GitHub;

/// <summary>
///     Verifies that workflow progress precedes completed-run log output.
/// </summary>
public sealed class GitHubWorkflowMonitorTests
{
    /// <summary>
    ///     Ensures no job text is requested before the run and its final step finish.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Completed_run_logs_follow_live_job_and_step_progress()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AutoMate.Deployments",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        await dbContext.Database.EnsureCreatedAsync();

        var deployment = new Deployment
        {
            CsProject = new CsProject
            {
                Name = "Web",
                Path = "Web/Web.csproj",
                Application = new Domain.Entities.Application
                {
                    Name = "Sample",
                    SourceType = SourceType.Remote,
                    SourcePathOrUrl = "https://github.com/example/sample",
                    User = new LocalUser { Username = "test", Email = "test@example.invalid" }
                }
            }
        };
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        var publisher = new RecordingPublisher();
        var github = new WorkflowSequenceGitHubService(publisher);
        var monitor = new GitHubWorkflowMonitor(dbContext, github, publisher, new PassThroughRedactor(),
            new GitHubWorkflowMonitoringOptions { PollIntervalSeconds = 1, MaximumMonitoringMinutes = 1 },
            NullLogger<GitHubWorkflowMonitor>.Instance);
        var request = new CloudDeploymentRequestDto
        {
            Config = new DeploymentConfigDto { ProjectId = Guid.NewGuid() },
            GitHubAccessToken = "token",
            RepositoryOwner = "example",
            RepositoryName = "sample"
        };

        await monitor.StreamBuildLogAsync(deployment.Id, request.Config.ProjectId, "preparation");
        publisher.Events.Single().DeploymentId.Should().Be(deployment.Id);

        var run = await monitor.PollWorkflowRunAsync(request, deployment, "commit", CancellationToken.None);

        var ownedSpans = spans.Where(span => Equals(span.GetTagItem("deployment.id"), deployment.Id)).ToArray();
        Assert.Contains(ownedSpans,
            span => span.OperationName == "github.workflow.query" && span.Status == ActivityStatusCode.Ok);
        Assert.Contains(ownedSpans, span => span.OperationName == "github.jobs.query");
        Assert.Contains(ownedSpans, span => span.OperationName == "github.logs.download");
        Assert.Single(ownedSpans.Select(span => span.TraceId).Distinct());
        Assert.All(ownedSpans, span =>
        {
            Assert.Equal(request.Config.ProjectId, span.GetTagItem("deployment.project.id"));
            Assert.Empty(span.Events);
            Assert.Null(span.StatusDescription);
        });
        run!.Status.Should().Be("completed");
        github.JobLogRequests.Should().Be(1);
        github.JobLogRequestedAfterCompletedRun.Should().BeTrue();
        publisher.Events.Select(item => item.Kind).Should().ContainInOrder(
            DeploymentDiagnosticKind.JobState, DeploymentDiagnosticKind.StepState,
            DeploymentDiagnosticKind.JobState, DeploymentDiagnosticKind.StepState,
            DeploymentDiagnosticKind.Log);
        publisher.Events.Last(item => item.Kind == DeploymentDiagnosticKind.Log).Message.Should()
            .Be("build output\r\n");
    }

    /// <summary>
    ///     Captures diagnostics in publication order for assertions.
    /// </summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher
    {
        /// <summary>Diagnostics emitted by the monitor in delivery order.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <summary>
        ///     Records normalized fixture observations and reports the configured publication outcome.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task completing after the fixture operation and its awaited callbacks finish.</returns>
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     Leaves test diagnostics unchanged so ordering can be asserted directly.
    /// </summary>
    private sealed class PassThroughRedactor : IDiagnosticRedactor
    {
        /// <summary>
        ///     Returns fixture text unchanged so normalization assertions observe the supplied content.
        /// </summary>
        /// <param name="value">Synthetic input text.</param>
        /// <param name="maximumCharacters">The maximum permitted text length; ignored by this fixture.</param>
        /// <returns>The supplied fixture value.</returns>
        public string RedactText(string value, int maximumCharacters = 4096)
        {
            return value;
        }

        /// <summary>
        ///     Returns the original synthetic event with zero masking counts.
        /// </summary>
        /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
        /// <returns>The configured redaction result instance or response for this scenario.</returns>
        public RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent)
        {
            return new RedactionResult(diagnosticEvent, 0);
        }
    }

    /// <summary>
    ///     Returns one running snapshot followed by a completed snapshot and job log.
    /// </summary>
    /// <param name="publisher">Accepts normalized observations for bounded redaction and durable delivery.</param>
    private sealed class WorkflowSequenceGitHubService(RecordingPublisher publisher) : IGitHubService
    {
        /// <summary>Number of workflow status requests made by the monitor.</summary>
        private int _runPolls;

        /// <summary>Number of completed-job log requests.</summary>
        public int JobLogRequests { get; private set; }

        /// <summary>Whether the download followed the completed step diagnostic.</summary>
        public bool JobLogRequestedAfterCompletedRun { get; private set; }

        /// <summary>
        ///     Supplies no matching deployment commit in the workflow-sequence fixture.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="branchName">
        ///     The repository branch used for deployment assets or workflow selection; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="marker">
        ///     The stable deployment marker used to find an existing generated commit; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning null for every fixture call.</returns>
        public Task<string?> FindDeploymentCommitAsync(string accessToken, string repoOwner, string repoName,
            string branchName, string marker, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <summary>
        ///     Advances the synthetic workflow from in-progress to successful completion on successive
        ///     polls.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="workflowFileName">The workflow filename selected for dispatch or lookup; ignored by this fixture.</param>
        /// <param name="branchName">
        ///     The repository branch used for deployment assets or workflow selection; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="headSha">
        ///     The optional commit identity used to restrict workflow-run lookup; ignored by this fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>
        ///     A task returning the configured git hub workflow run dto result, or null when the fixture
        ///     supplies no value.
        /// </returns>
        public Task<GitHubWorkflowRunDto?> GetLatestWorkflowRunAsync(string accessToken, string repoOwner,
            string repoName, string workflowFileName, string branchName, string? headSha = null,
            CancellationToken cancellationToken = default)
        {
            _runPolls++;
            return Task.FromResult<GitHubWorkflowRunDto?>(new GitHubWorkflowRunDto
            {
                Id = 123,
                Status = _runPolls == 1 ? "in_progress" : "completed",
                Conclusion = _runPolls == 1 ? null : "success"
            });
        }

        /// <summary>
        ///     Supplies job and step states matching the controlled workflow poll phase.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="runId">Verified durable cloud or provider workflow-run identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured synthetic collection or recorded fixture selection.</returns>
        public Task<IReadOnlyList<GitHubWorkflowJobDto>> GetWorkflowJobsAsync(string accessToken, string repoOwner,
            string repoName, long runId, CancellationToken cancellationToken = default)
        {
            var completed = _runPolls > 1;
            IReadOnlyList<GitHubWorkflowJobDto> jobs =
            [
                new()
                {
                    Id = 456,
                    Name = "deploy",
                    Status = completed ? "completed" : "in_progress",
                    Conclusion = completed ? "success" : null,
                    Steps =
                    [
                        new GitHubWorkflowStepDto
                        {
                            Number = 1,
                            Name = "Build image",
                            Status = completed ? "completed" : "in_progress",
                            Conclusion = completed ? "success" : null
                        }
                    ]
                }
            ];
            return Task.FromResult(jobs);
        }

        /// <summary>
        ///     Records log-download ordering and returns the configured synthetic job output.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="jobId">The GitHub workflow job identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured git hub workflow job log download instance or response for this scenario.</returns>
        public Task<GitHubWorkflowJobLogDownload> DownloadWorkflowJobLogsAsync(string accessToken, string repoOwner,
            string repoName, long jobId, CancellationToken cancellationToken = default)
        {
            JobLogRequests++;
            JobLogRequestedAfterCompletedRun = _runPolls > 1 &&
                                               publisher.Events.Any(item =>
                                                   item.Kind == DeploymentDiagnosticKind.StepState &&
                                                   item.Message.Contains("completed/success",
                                                       StringComparison.Ordinal));
            return Task.FromResult(new GitHubWorkflowJobLogDownload(GitHubWorkflowLogAvailability.Available,
                "build output"));
        }

        /// <summary>
        ///     Supplies an empty run-log archive in the workflow-sequence fixture.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="runId">Verified durable cloud or provider workflow-run identity; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning an empty synthetic result collection.</returns>
        public Task<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>> DownloadWorkflowRunLogEntriesAsync(
            string accessToken, string repoOwner, string repoName, long runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>>([]);
        }

        /// <summary>
        ///     Supplies an empty repository catalog without contacting GitHub.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="forceRefresh">Whether repository caching is bypassed; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the configured synthetic collection or recorded fixture selection.</returns>
        public Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<GitHubRepositoryDto>());
        }

        /// <summary>
        ///     Supplies a fixed synthetic commit identity without writing repository files.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="files">
        ///     The generated files to commit, with their repository-relative destinations; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="branchName">
        ///     The repository branch used for deployment assets or workflow selection; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="commitMessage">Synthetic repository commit message; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>A task returning the synthetic text produced for this scenario.</returns>
        public Task<string> CommitCloudDeploymentFilesAsync(string accessToken, string repoOwner, string repoName,
            List<TemplateFile> files, string branchName = "automate/azure-deployment",
            string commitMessage = "Add AutoMate Azure deployment workflow",
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("commit");
        }

        /// <summary>
        ///     Completes immediately without executing live upsert repository secrets work.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="secrets">
        ///     The repository secret values to encrypt before submission; never log them; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task UpsertRepositorySecretsAsync(string accessToken, string repoOwner, string repoName,
            IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live dispatch workflow work.
        /// </summary>
        /// <param name="accessToken">The provider access token; never include it in diagnostics; ignored by this fixture.</param>
        /// <param name="repoOwner">The GitHub repository owner or organization; ignored by this fixture.</param>
        /// <param name="repoName">The GitHub repository name; ignored by this fixture.</param>
        /// <param name="workflowFileName">The workflow filename selected for dispatch or lookup; ignored by this fixture.</param>
        /// <param name="branchName">
        ///     The repository branch used for deployment assets or workflow selection; ignored by this
        ///     fixture.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task DispatchWorkflowAsync(string accessToken, string repoOwner, string repoName,
            string workflowFileName, string branchName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
