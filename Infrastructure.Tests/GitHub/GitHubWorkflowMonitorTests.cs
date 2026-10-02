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

namespace Infrastructure.Tests.GitHub;

/// <summary>Verifies that workflow progress precedes completed-run log output.</summary>
public sealed class GitHubWorkflowMonitorTests
{
    /// <summary>Ensures no job text is requested before the run and its final step finish.</summary>
    [Fact]
    public async Task Completed_run_logs_follow_live_job_and_step_progress()
    {
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

    /// <summary>Captures diagnostics in publication order for assertions.</summary>
    private sealed class RecordingPublisher : IDeploymentDiagnosticPublisher
    {
        /// <summary>Diagnostics emitted by the monitor in delivery order.</summary>
        public List<DeploymentDiagnosticEvent> Events { get; } = [];

        /// <inheritdoc />
        public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Leaves test diagnostics unchanged so ordering can be asserted directly.</summary>
    private sealed class PassThroughRedactor : IDiagnosticRedactor
    {
        /// <inheritdoc />
        public RedactionResult Redact(DeploymentDiagnosticEvent diagnosticEvent)
        {
            return new RedactionResult(diagnosticEvent, 0);
        }
    }

    /// <summary>Returns one running snapshot followed by a completed snapshot and job log.</summary>
    private sealed class WorkflowSequenceGitHubService(RecordingPublisher publisher) : IGitHubService
    {
        /// <summary>Number of workflow status requests made by the monitor.</summary>
        private int _runPolls;

        /// <summary>Number of completed-job log requests.</summary>
        public int JobLogRequests { get; private set; }

        /// <summary>Whether the download followed the completed step diagnostic.</summary>
        public bool JobLogRequestedAfterCompletedRun { get; private set; }

        /// <inheritdoc />
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

        /// <inheritdoc />
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

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>> DownloadWorkflowRunLogEntriesAsync(
            string accessToken, string repoOwner, string repoName, long runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>>([]);
        }

        /// <inheritdoc />
        public Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<GitHubRepositoryDto>());
        }

        /// <inheritdoc />
        public Task<string> CommitCloudDeploymentFilesAsync(string accessToken, string repoOwner, string repoName,
            List<TemplateFile> files, string branchName = "automate/azure-deployment",
            string commitMessage = "Add AutoMate Azure deployment workflow",
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("commit");
        }

        /// <inheritdoc />
        public Task UpsertRepositorySecretsAsync(string accessToken, string repoOwner, string repoName,
            IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DispatchWorkflowAsync(string accessToken, string repoOwner, string repoName,
            string workflowFileName, string branchName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}