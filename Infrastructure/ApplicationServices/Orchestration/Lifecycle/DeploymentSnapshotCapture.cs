using System.Text.Json;
using Domain.DTO;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Orchestration;

/// <summary>Captures an explicit whitelist of non-secret launch settings once per deployment.</summary>
internal static class DeploymentSnapshotCapture
{
    /// <summary>Adds discovered artifact facts without reading or replacing current project settings.</summary>
    internal static void Resolve(Deployment deployment, string? runtime = null, string? commit = null)
    {
        if (deployment.ConfigurationSnapshotJson is not { } json) return;
        var snapshot = JsonSerializer.Deserialize<DeploymentConfigurationSnapshot>(json)!;
        deployment.ConfigurationSnapshotJson = JsonSerializer.Serialize(snapshot with
        {
            Runtime = string.IsNullOrWhiteSpace(runtime) ? snapshot.Runtime : runtime,
            Commit = commit ?? snapshot.Commit,
            Image = deployment.ImageTag,
            WorkflowFile = snapshot.SourceUrl is null ? null : "deploy.yml"
        });
    }

    /// <summary>Captures initial settings before provider side effects; recovery never overwrites an existing snapshot.</summary>
    internal static async Task CaptureAsync(AutoMateDbContext db, Deployment deployment, DeploymentConfigDto config,
        string? source, string? branch, CancellationToken token)
    {
        if (deployment.ConfigurationSnapshotJson is not null) return;
        var project = await db.CsProjects.AsNoTracking().Include(p => p.Application).Include(p => p.Configuration)
            .SingleAsync(p => p.Id == deployment.CsProjectId, token);
        source ??= project.Application.SourcePathOrUrl;
        var sourceUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetLeftPart(UriPartial.Path)
            : null;
        deployment.ConfigurationSnapshotJson = JsonSerializer.Serialize(new DeploymentConfigurationSnapshot(
            project.Name, config.IsCloudDeployment ? "Azure Container Apps" : "Docker Compose", sourceUrl, branch, null,
            config.EnvironmentName, project.Configuration?.DotNetVersion, config.ExposedPort,
            project.Configuration?.IsPublic ?? false, config.IsCloudDeployment ? config.CloudAzureRegion : null,
            config.IsCloudDeployment ? config.CloudResourceGroupName : null,
            config.IsCloudDeployment ? config.CloudContainerAppName : null,
            config.IsCloudDeployment ? config.CloudRegistryName : null,
            Databases: config.Databases
                .Select(d => new DeploymentDatabaseSnapshot(d.DbType, d.DbName, d.ContainerNameSuffix)).ToArray()));
    }
}