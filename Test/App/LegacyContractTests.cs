using System.Text.Json;
using Application.Abstractions.Diagnostics.Events;
using Domain.DTO.GitHub;
using Domain.Entities.Deployments;
using Domain.Entities.Diagnostics;
using Domain.Enums.GitHub;
using Domain.Enums.Projects;

namespace Test.App;

/// <summary>Protects persisted enum ordinals and retained metadata shapes from accidental removal.</summary>
public sealed class LegacyContractTests
{
    /// <summary>Persisted application classifications and default diagnostic states retain their numeric representations.</summary>
    [Fact]
    public void Persisted_enum_values_remain_stable()
    {
        Assert.Equal(0, (int)AppType.WebApi);
        Assert.Equal(1, (int)AppType.Blazor);
        Assert.Equal(2, (int)AppType.Mvc);
        Assert.Equal(0, (int)GitHubWorkflowLogAvailability.Unknown);
        Assert.Equal(1, (int)DeploymentDiagnosticComponent.Daemon);
    }

    /// <summary>Legacy redacted snapshot metadata retains expiry, trace and deployment linkage during JSON readback.</summary>
    [Fact]
    public void Legacy_snapshot_and_repository_identity_survive_readback()
    {
        var id = Guid.NewGuid();
        var expiry = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var expected = new DeploymentDiagnosticSnapshot
        {
            DeploymentId = id,
            Deployment = new Deployment { Id = id },
            RedactedPayload = "safe legacy context",
            TraceId = "synthetic-trace",
            ExpiresAt = expiry
        };
        var actual = JsonSerializer.Deserialize<DeploymentDiagnosticSnapshot>(JsonSerializer.Serialize(expected))!;
        Assert.Equal(expected.DeploymentId, actual.DeploymentId);
        Assert.Equal(expected.Deployment.Id, actual.Deployment.Id);
        Assert.Equal(expected.RedactedPayload, actual.RedactedPayload);
        Assert.Equal(expected.TraceId, actual.TraceId);
        Assert.Equal(expected.ExpiresAt, actual.ExpiresAt);
        var expectedRepository = new GitHubRepositoryDto { Id = 123 };
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(expectedRepository));
        Assert.Equal(expectedRepository.Id, wire.RootElement.GetProperty("id").GetInt64());
        var repository = JsonSerializer.Deserialize<GitHubRepositoryDto>("""{"id":123,"private":true}""")!;
        Assert.Equal(expectedRepository.Id, repository.Id);
    }
}
