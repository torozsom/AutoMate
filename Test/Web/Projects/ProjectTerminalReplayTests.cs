using System.Reflection;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using ProjectDetails = Web.Components.Pages.Projects.ProjectDetails;

namespace Test.Web.Projects;

/// <summary>
///     Checks active-only replay and delayed-reply fences without a live SignalR connection.
/// </summary>
public sealed class ProjectTerminalReplayTests
{
    /// <summary>
    ///     Inactive history belongs on historical pages; active deployments can replay.
    /// </summary>
    /// <param name="status">Deployment state under test.</param>
    /// <param name="expected">Expected outcome for this scenario.</param>
    [Theory]
    [InlineData(DeploymentStatus.Starting, true)]
    [InlineData(DeploymentStatus.Running, true)]
    [InlineData(DeploymentStatus.Stopped, false)]
    [InlineData(DeploymentStatus.Failed, false)]
    public void Replay_checks_status_route_deployment_and_generation(DeploymentStatus status, bool expected)
    {
        var project = Guid.NewGuid();
        var deployment = new Deployment { Status = status };
        var page = new ProjectDetails();
        typeof(ProjectDetails).GetProperty("ProjectId")!.SetValue(page, project);
        Set(page, "_app", new Domain.Entities.Projects.Application
        {
            Id = project,
            Name = "fixture",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "fixture",
            CsProjects = [new CsProject { Deployments = [deployment] }]
        });
        Set(page, "_terminalDeploymentId", (Guid?)deployment.Id);
        Set(page, "_pageGeneration", 2L);
        Assert.Equal(expected, Current(page, project, deployment.Id, 2));
        Assert.False(Current(page, Guid.NewGuid(), deployment.Id, 2));
        Assert.False(Current(page, project, Guid.NewGuid(), 2));
        Assert.False(Current(page, project, deployment.Id, 1));
        Set(page, "_analysisDisposed", true);
        Assert.False(Current(page, project, deployment.Id, 2));
    }

    /// <summary>
    ///     Uses the same acceptance check as each awaited terminal replay boundary.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <param name="project">Synthetic project identity.</param>
    /// <param name="deployment">Synthetic deployment identity.</param>
    /// <param name="generation">Page generation captured before the asynchronous read.</param>
    /// <returns>The configured fixture condition or completion outcome.</returns>
    private static bool Current(ProjectDetails page, Guid project, Guid deployment, long generation)
    {
        return (bool)typeof(ProjectDetails).GetMethod("IsCurrentTerminalRead",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [project, (Guid?)deployment, generation])!;
    }

    /// <summary>
    ///     Seeds current deployment state without starting provider subscriptions.
    /// </summary>
    /// <param name="page">Fixture page component under test.</param>
    /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
    /// <param name="value">Fixture member value to assign.</param>
    private static void Set(ProjectDetails page, string name, object value)
    {
        typeof(ProjectDetails)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    }
}
