using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Application.Abstractions.GitHub;
using Application.Orchestration;
using Infrastructure.GitHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Web.Configs;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies profile-specific validation using the production Web composition root.</summary>
public sealed class HostingProfileRegistrationTests
{
    /// <summary>Dashboard dependencies resolve on self-hosted installations without SaaS credentials.</summary>
    [Fact]
    public void SelfHosted_resolves_shared_cloud_services_without_GitHub_App_settings()
    {
        var builder = CreateBuilder("SelfHosted");
        builder.AddApplicationServices();
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGitHubAppCredentials>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICloudDeploymentRunService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICloudDeploymentOrchestrator>());
    }

    /// <summary>SaaS still rejects missing GitHub App secrets during startup validation.</summary>
    [Fact]
    public void SaaS_startup_requires_GitHub_App_settings()
    {
        var certificatePath = Path.Combine(Path.GetTempPath(), $"automate-profile-{Guid.NewGuid():N}.pfx");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=AutoMate test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "test-only"));
        try
        {
            var builder = CreateBuilder("SaaS");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SaaS:DataProtectionCertificatePath"] = certificatePath,
                ["SaaS:DataProtectionCertificatePassword"] = "test-only"
            });
            builder.AddApplicationServices();
            using var services = builder.Services.BuildServiceProvider();

            var failure = Assert.Throws<OptionsValidationException>(() =>
                services.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains("configured GitHub App", failure.Message);
        }
        finally { File.Delete(certificatePath); }
    }

    /// <summary>Uses isolated configuration and fake connection settings without contacting providers.</summary>
    private static WebApplicationBuilder CreateBuilder(string mode)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(ServiceConfiguration).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostingProfile:Mode"] = mode,
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused;Username=unused",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["Authentication:GitHub:ClientId"] = "test-client",
            ["Authentication:GitHub:ClientSecret"] = "test-secret",
            ["Authentication:Microsoft:ClientId"] = "test-client",
            ["Authentication:Microsoft:ClientSecret"] = "test-secret"
        });
        return builder;
    }
}
