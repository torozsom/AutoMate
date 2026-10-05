using System.Reflection;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Checks deployment preparation failures at the real component boundary.</summary>
public sealed class DeploymentFailurePrivacyTests
{
    /// <summary>Scanner errors leave configuration closed and show authored guidance without private filesystem text.</summary>
    [Fact]
    public async Task Scanner_failure_does_not_render_exception_text()
    {
        var component = new Dashboard();
        var scanner = DispatchProxy.Create<IProjectScannerService, OperationalLoggingTests.PortProxy>();
        ((OperationalLoggingTests.PortProxy)scanner).Call = (_, _) =>
            Task.FromException<DeploymentConfigDto>(new IOException("private-path-password"));
        var capabilities = DispatchProxy.Create<IDeploymentCapabilities, OperationalLoggingTests.PortProxy>();
        ((OperationalLoggingTests.PortProxy)capabilities).Call = (_, _) => true;
        var type = typeof(Dashboard);
        type.GetProperty("ProjectScanner", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(component, scanner);
        type.GetProperty("DeploymentCapabilities", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(component, capabilities);
        var app = new Domain.Entities.Application
        {
            Name = "fixture",
            SourceType = SourceType.Local,
            SourcePathOrUrl = "private-path",
            CsProjects = [new CsProject { IsWebProject = true, Path = "private-path" }]
        };
        await (Task)type.GetMethod("DeployAppAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(component, [app])!;
        var message = (string)type.GetField("_globalErrorMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(component)!;
        Assert.Contains("Verify the project files", message);
        Assert.DoesNotContain("private", message);
        Assert.False((bool)type.GetField("_showConfigModal", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(component)!);
    }
}