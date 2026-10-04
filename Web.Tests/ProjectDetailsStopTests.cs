using System.Reflection;
using Domain.Enums;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Cloud projects must never enter the local filesystem/Docker stop path.</summary>
public sealed class ProjectDetailsStopTests
{
    /// <summary>A remote project with its relative provider path is rejected before local services are consulted.</summary>
    [Fact]
    public async Task Remote_project_stop_does_not_enqueue_a_local_job()
    {
        var component = new ProjectDetails();
        var type = typeof(ProjectDetails);
        type.GetField("_app", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(component,
            new Domain.Entities.Application { Name = "Cloud app", SourceType = SourceType.Remote, SourcePathOrUrl = "https://github.com/example/app" });
        // No local services are injected: entering that path would fail this test.
        var stop = type.GetMethod("StopDeploymentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)stop.Invoke(component, null)!;
        Assert.Equal("Stop cloud deployments in your cloud provider's portal.",
            type.GetField("_workflowStatusMessage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(component));
    }
}
