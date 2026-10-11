using Application.Data.Apps;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies exact-project consent persistence and authorization without diagnostic payload storage.</summary>
public sealed class AnalysisConsentTests
{
    /// <summary>Remote projects without configuration support explicit consent, durable readback and revocation.</summary>
    [Fact]
    public async Task Remote_project_without_configuration_can_grant_and_revoke_consent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var owner = new LocalUser { Username = "fixture", Email = "fixture@example.invalid" };
        var app = new Domain.Entities.Application
            { Name = "fixture", SourcePathOrUrl = "fixture", SourceType = SourceType.Remote, User = owner };
        var selected = new CsProject { Application = app };
        var sibling = new CsProject { Application = app };
        db.CsProjects.AddRange(selected, sibling);
        await db.SaveChangesAsync();
        var service = new ApplicationService(db, NullLogger<ApplicationService>.Instance);
        Assert.False(await service.SetAiDiagnosticEgressConsentAsync(app.Id, Guid.NewGuid(), selected.Id, true));
        Assert.False(await service.SetAiDiagnosticEgressConsentAsync(Guid.NewGuid(), owner.Id, selected.Id, true));
        Assert.Empty(await db.Set<Configuration>().ToListAsync());
        Assert.True(await service.SetAiDiagnosticEgressConsentAsync(app.Id, owner.Id, selected.Id, true));
        await using var read = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        var readService = new ApplicationService(read, NullLogger<ApplicationService>.Instance);
        var saved = await readService.GetAppByIdAsync(app.Id, owner.Id);
        Assert.True(saved!.CsProjects.Single(item => item.Id == selected.Id).Configuration!
            .AiDiagnosticEgressConsented);
        Assert.Null(saved.CsProjects.Single(item => item.Id == sibling.Id).Configuration);
        Assert.True(await readService.SetAiDiagnosticEgressConsentAsync(app.Id, owner.Id, selected.Id, false));
        db.ChangeTracker.Clear();
        Assert.False((await db.Set<Configuration>().SingleAsync()).AiDiagnosticEgressConsented);
    }

    /// <summary>Consent affects only the requested project and survives a fresh context, including revocation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_project_consent_is_persisted(bool consented)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var owner = new LocalUser { Username = "fixture", Email = "fixture@example.invalid" };
        var app = new Domain.Entities.Application
            { Name = "fixture", SourcePathOrUrl = "fixture", SourceType = SourceType.Local, User = owner };
        var other = new CsProject
        {
            Application = app,
            Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = !consented }
        };
        var selected = new CsProject
        {
            Application = app,
            Configuration = new Configuration { DotNetVersion = "net10.0", AiDiagnosticEgressConsented = !consented }
        };
        db.CsProjects.AddRange(other, selected);
        await db.SaveChangesAsync();
        var service = new ApplicationService(db, NullLogger<ApplicationService>.Instance);
        Assert.True(await service.SetAiDiagnosticEgressConsentAsync(app.Id, owner.Id, selected.Id, consented));
        await using var read = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        Assert.Equal(consented,
            (await read.Set<Configuration>().SingleAsync(item => item.CsProjectId == selected.Id))
            .AiDiagnosticEgressConsented);
        Assert.Equal(!consented,
            (await read.Set<Configuration>().SingleAsync(item => item.CsProjectId == other.Id))
            .AiDiagnosticEgressConsented);
    }

    /// <summary>
    ///     Wrong owners, application/project mismatches and absent configurations cannot update another project's
    ///     consent.
    /// </summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("application")]
    [InlineData("project")]
    [InlineData("configuration")]
    public async Task Unauthorized_or_missing_target_leaves_consent_unchanged(string invalid)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var owner = new LocalUser { Username = "fixture", Email = "fixture@example.invalid" };
        var app = new Domain.Entities.Application
            { Name = "fixture", SourcePathOrUrl = "fixture", SourceType = SourceType.Local, User = owner };
        var selected = new CsProject
        {
            Application = app,
            Configuration = invalid == "configuration" ? null : new Configuration { DotNetVersion = "net10.0" }
        };
        db.CsProjects.Add(selected);
        await db.SaveChangesAsync();
        var service = new ApplicationService(db, NullLogger<ApplicationService>.Instance);
        Assert.False(await service.SetAiDiagnosticEgressConsentAsync(invalid == "application" ? Guid.NewGuid() : app.Id,
            invalid == "owner" ? Guid.NewGuid() : owner.Id, invalid == "project" ? Guid.NewGuid() : selected.Id, true));
        db.ChangeTracker.Clear();
        Assert.DoesNotContain(await db.Set<Configuration>().ToListAsync(), item => item.AiDiagnosticEgressConsented);
    }
}