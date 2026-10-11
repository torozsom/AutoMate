using Domain.Enums;
using Infrastructure.Data;
using Npgsql;

namespace Infrastructure.Tests.Ai;

/// <summary>
///     Opt-in execution of the exact production PostgreSQL trigger in a disposable schema, without applying
///     migrations.
/// </summary>
public sealed class FailedDeploymentTriggerPostgresTests
{
    /// <summary>Proves real transition capture, bulk writes, rollback, duplicate suppression and deployment cascade deletion.</summary>
    [AiPostgresFact]
    public async Task Production_trigger_captures_only_committed_failure_once()
    {
        Assert.Equal(3, (int)DeploymentStatus.Failed);
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("AUTOMATE_AI_TEST_DB"));
        await connection.OpenAsync();
        var schema = "ai_failure_test_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE SCHEMA {schema}; SET search_path TO {schema};");
        try
        {
            await ExecuteAsync(connection, """
                                           CREATE TABLE deployments (id uuid PRIMARY KEY, status integer NOT NULL);
                                           CREATE TABLE failed_deployment_analysis_events (
                                               deployment_id uuid PRIMARY KEY REFERENCES deployments(id) ON DELETE CASCADE,
                                               created_at timestamptz NOT NULL, completed_at timestamptz NULL);
                                           """);
            await ExecuteAsync(connection, FailedDeploymentAnalysisTrigger.CreateSql);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            await ExecuteAsync(connection, $"INSERT INTO deployments VALUES ('{first}', 0), ('{second}', 0);");
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, $"UPDATE deployments SET status = 3 WHERE id = '{first}';");
                Assert.Equal(1L, await CountAsync(connection));
                await transaction.RollbackAsync();
            }

            Assert.Equal(0L, await CountAsync(connection));
            await ExecuteAsync(connection, "UPDATE deployments SET status = 3;");
            Assert.Equal(2L, await CountAsync(connection));
            await ExecuteAsync(connection,
                "UPDATE failed_deployment_analysis_events SET completed_at = CURRENT_TIMESTAMP;");
            await ExecuteAsync(connection,
                "UPDATE deployments SET status = 3; UPDATE deployments SET status = 1; UPDATE deployments SET status = 3;");
            Assert.Equal(2L, await CountAsync(connection));
            await using (var command =
                         new NpgsqlCommand(
                             "SELECT COUNT(*) FROM failed_deployment_analysis_events WHERE completed_at IS NULL",
                             connection))
            {
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }

            var insertedFailed = Guid.NewGuid();
            await ExecuteAsync(connection, $"INSERT INTO deployments VALUES ('{insertedFailed}', 3);");
            Assert.Equal(3L, await CountAsync(connection));
            await ExecuteAsync(connection, "DELETE FROM deployments;");
            Assert.Equal(0L, await CountAsync(connection));
        }
        finally
        {
            await ExecuteAsync(connection, $"SET search_path TO public; DROP SCHEMA {schema} CASCADE;");
        }
    }

    /// <summary>Executes authored SQL only in the test's uniquely generated schema.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads the total number of durable metadata markers.</summary>
    private static async Task<long> CountAsync(NpgsqlConnection connection)
    {
        await using var command =
            new NpgsqlCommand("SELECT COUNT(*) FROM failed_deployment_analysis_events", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>Never connects to an application database during default test runs.</summary>
public sealed class AiPostgresFactAttribute : FactAttribute
{
    /// <summary>Requires an explicitly configured isolated PostgreSQL test database.</summary>
    public AiPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUTOMATE_AI_TEST_DB")))
            Skip = "Set AUTOMATE_AI_TEST_DB to an isolated PostgreSQL test database.";
    }
}