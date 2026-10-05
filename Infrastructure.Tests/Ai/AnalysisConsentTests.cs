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