using System.Text.Json;
using Domain.DTO.Projects;

namespace Test.Infra.Scanning;

/// <summary>Protects mutable discovery contracts and case-insensitive dependency aggregation.</summary>
public sealed class ProjectMetadataContractTests
{
    /// <summary>Dependency aggregation treats casing variants as the same package and project path.</summary>
    [Fact]
    public void Dependency_graph_collections_preserve_case_insensitive_updates()
    {
        var metadata = new ProjectMetadataDto { UserSecretsId = "synthetic-store-id" };
        metadata.ReferencedProjectPackages.Add("Example.Package", "1.0");
        metadata.ReferencedProjectPackages["example.package"] = "2.0";
        Assert.Equal("2.0", Assert.Single(metadata.ReferencedProjectPackages).Value);
        Assert.True(metadata.AllProjectPaths.Add("C:/Fixture/Web.csproj"));
        Assert.False(metadata.AllProjectPaths.Add("c:/fixture/web.csproj"));
        Assert.Single(metadata.AllProjectPaths);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        Assert.Equal(metadata.UserSecretsId, json.RootElement.GetProperty("UserSecretsId").GetString());
    }

    /// <summary>Provider rule package lists remain mutable and local discovery retains its .NET flag.</summary>
    [Fact]
    public void Discovery_and_catalog_payloads_retain_package_and_project_metadata()
    {
        var rule = new DbProviderRuleDto { DbType = "Postgres" };
        rule.Packages.Add("Npgsql");
        var restored = JsonSerializer.Deserialize<DbProviderRuleDto>(JsonSerializer.Serialize(rule))!;
        Assert.Equal("Npgsql", Assert.Single(restored.Packages));
        var project = JsonSerializer.Deserialize<LocalProjectDto>(
            JsonSerializer.Serialize(new LocalProjectDto { IsDotNetProject = true }))!;
        Assert.True(project.IsDotNetProject);
    }
}
