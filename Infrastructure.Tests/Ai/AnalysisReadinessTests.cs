using System.Data.Common;
using Application.Abstractions.Ai;
using Application.Ai;
using Infrastructure.Ai;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies local policy, credential reload and real read-only queue/schema access without provider resolution.</summary>
public sealed class AnalysisReadinessTests
{
    /// <summary>Current configuration distinguishes disabled admission, blocked egress and locally ready providers.</summary>
    [Theory]
    [InlineData(false, false, false, false, AnalysisConfigurationState.Disabled)]
    [InlineData(true, false, true, true, AnalysisConfigurationState.EgressDisabled)]
    [InlineData(true, true, false, true, AnalysisConfigurationState.Unavailable)]
    [InlineData(true, true, true, false, AnalysisConfigurationState.Unavailable)]
    [InlineData(true, true, true, true, AnalysisConfigurationState.Ready)]
    public async Task Configuration_and_empty_queue_are_reported_without_provider_resolution(bool enabled, bool egress,
        bool approved, bool credentials, AnalysisConfigurationState expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new ReadOnlyCommands();
        await using var db = CreateDb(connection, commands);
        await db.Database.EnsureCreatedAsync();
        commands.EnforceReadOnly = true;
        var monitor = new SettingsMonitor { CurrentValue = Settings(enabled, egress, approved) };
        var catalog = Catalog(() => credentials);
        var service = new DeploymentAnalysisReadinessService(db, monitor, catalog);
        var result = await service.CheckAsync();
        Assert.Equal(expected, result.Configuration);
        Assert.True(result.QueueAvailable);
        Assert.Equal(3, commands.Reads);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Repeated probes use current policy and credential presence rather than caching an earlier approval.</summary>
    [Fact]
    public async Task Policy_and_credentials_are_rechecked_and_validation_failures_are_safe()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var monitor = new SettingsMonitor { CurrentValue = Settings(true, true, true) };
        var credentials = true;
        var service = new DeploymentAnalysisReadinessService(db, monitor, Catalog(() => credentials));
        Assert.Equal(AnalysisConfigurationState.Ready, (await service.CheckAsync()).Configuration);
        credentials = false;
        Assert.Equal(AnalysisConfigurationState.Unavailable, (await service.CheckAsync()).Configuration);
        monitor.CurrentValue = Settings(true, false, true);
        Assert.Equal(AnalysisConfigurationState.EgressDisabled, (await service.CheckAsync()).Configuration);
        monitor.Failure = new OptionsValidationException("private-name", typeof(AiAnalysisOptions), ["private-secret"]);
        var invalid = await service.CheckAsync();
        Assert.Equal(AnalysisConfigurationState.Invalid, invalid.Configuration);
        Assert.True(invalid.QueueAvailable);
        Assert.DoesNotContain("private", invalid.ToString());
    }

    /// <summary>A missing queue, wakeup or analysis schema is unavailable even when a plain database connection succeeds.</summary>
    [Theory]
    [InlineData("DeploymentAnalysisWorkItems")]
    [InlineData("FailedDeploymentAnalysisEvents")]
    [InlineData("AiDeploymentAnalyses")]
    [InlineData("AiAnalysisBudgetEntries")]
    public async Task Missing_metadata_schema_is_unavailable_even_with_disabled_ai(string table)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var drop = table switch
        {
            "DeploymentAnalysisWorkItems" => "DROP TABLE \"DeploymentAnalysisWorkItems\"",
            "FailedDeploymentAnalysisEvents" => "DROP TABLE \"FailedDeploymentAnalysisEvents\"",
            "AiDeploymentAnalyses" => "DROP TABLE \"AiDeploymentAnalyses\"",
            "AiAnalysisBudgetEntries" => "DROP TABLE \"AiAnalysisBudgetEntries\"",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        await db.Database.ExecuteSqlRawAsync(drop);
        Assert.True(await db.Database.CanConnectAsync());
        var result = await new DeploymentAnalysisReadinessService(db, new SettingsMonitor(), Catalog(() => false))
            .CheckAsync();
        Assert.False(result.QueueAvailable);
        Assert.Equal(AnalysisConfigurationState.Disabled, result.Configuration);
    }

    /// <summary>Caller cancellation reaches the real relational operation and cannot become a successful probe.</summary>
    [Fact]
    public async Task Queue_operation_observes_cancellation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var commands = new ReadOnlyCommands { Cancel = cancellation };
        await using var db = CreateDb(connection, commands);
        await db.Database.EnsureCreatedAsync();
        commands.EnforceReadOnly = true;
        var service = new DeploymentAnalysisReadinessService(db, new SettingsMonitor(), Catalog(() => false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, commands.Reads);
    }

    /// <summary>Creates the production metadata model with an isolated in-memory relational database.</summary>
    private static AutoMateDbContext CreateDb(SqliteConnection connection, ReadOnlyCommands? commands = null)
    {
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection);
        if (commands is not null) options.AddInterceptors(commands);
        return new AutoMateDbContext(options.Options, new EphemeralDataProtectionProvider());
    }

    /// <summary>Supplies explicit shared policy while allowing approval denial to be tested separately.</summary>
    private static AiAnalysisOptions Settings(bool enabled, bool egress, bool approved)
    {
        return new AiAnalysisOptions
        {
            Enabled = enabled,
            ProviderEgressEnabled = egress,
            Provider = "fixture",
            RegionalProcessingApproved = approved,
            DailyTenantCostBudget = 10,
            MaximumProviderAttemptCost = 1,
            ProcessingRegion = "eu",
            ApprovedRegions = ["eu"],
            ApprovedTenantIds = [Guid.NewGuid()],
            AllowedDataCategories = ["logs", "metrics", "traceCorrelation"]
        };
    }

    /// <summary>A throwing adapter proves readiness uses only registration metadata and local credential checks.</summary>
    private static AnalysisProviderCatalog Catalog(Func<bool> credentials)
    {
        return new AnalysisProviderCatalog([
            new AnalysisProviderRegistration("fixture", typeof(UnreachableProvider), _ => true, credentials)
        ]);
    }

    /// <summary>Test adapter that cannot be instantiated or called by readiness.</summary>
    private sealed class UnreachableProvider : ILlmAnalysisProvider
    {
        /// <summary>Rejects any accidental construction.</summary>
        public UnreachableProvider()
        {
            throw new InvalidOperationException("Provider must not be resolved.");
        }

        /// <inheritdoc />
        public Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Provider must not be called.");
        }
    }

    /// <summary>Mutable monitor simulates policy reload and safe translation of validation failures.</summary>
    private sealed class SettingsMonitor : IOptionsMonitor<AiAnalysisOptions>
    {
        /// <summary>Current synthetic settings.</summary>
        private AiAnalysisOptions _settings = new();

        /// <summary>Optional validation failure injected during reads.</summary>
        public Exception? Failure { get; set; }

        /// <inheritdoc />
        public AiAnalysisOptions CurrentValue
        {
            get => Failure is null ? _settings : throw Failure;
            set => _settings = value;
        }

        /// <inheritdoc />
        public AiAnalysisOptions Get(string? name)
        {
            return CurrentValue;
        }

        /// <inheritdoc />
        public IDisposable? OnChange(Action<AiAnalysisOptions, string?> listener)
        {
            return null;
        }
    }

    /// <summary>Checks bounded SELECT-only probe commands and injects cancellation into the actual database call.</summary>
    private sealed class ReadOnlyCommands : DbCommandInterceptor
    {
        /// <summary>Allows schema creation before read-only enforcement begins.</summary>
        public bool EnforceReadOnly { get; set; }

        /// <summary>Observed bounded queue reads.</summary>
        public int Reads { get; private set; }

        /// <summary>Optional cancellation source triggered at relational execution.</summary>
        public CancellationTokenSource? Cancel { get; init; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (EnforceReadOnly)
            {
                Assert.StartsWith("SELECT", command.CommandText);
                Assert.Contains("LIMIT", command.CommandText);
                Reads++;
                Cancel?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.False(EnforceReadOnly, "Readiness must never write queue metadata.");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}