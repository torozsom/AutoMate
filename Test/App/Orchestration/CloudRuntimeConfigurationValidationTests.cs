using Application.Orchestration.Cloud;
using Domain.DTO.Deployment;

namespace Test.App.Orchestration;

/// <summary>
///     Characterizes the existing cloud form policy independently of private component members.
/// </summary>
public sealed class CloudRuntimeConfigurationValidationTests
{
    /// <summary>
    ///     Database errors retain precedence over invalid environment names and no settings are mutated.
    /// </summary>
    [Fact]
    public void First_database_error_precedes_environment_errors()
    {
        var database = new DatabaseConfigDto { DbType = "unsupported" };
        var config = new DeploymentConfigDto { Databases = [database] };

        Assert.Equal("Database engine 'unsupported' is not supported for Azure deployments.",
            CloudRuntimeConfigurationValidation.Validate(config, ["1INVALID"]));

        Assert.Same(database, Assert.Single(config.Databases));

        Assert.Equal("unsupported", database.DbType);
    }

    /// <summary>
    ///     Supported aliases, credentials, duplicate names and generated-key collisions retain their existing policy.
    /// </summary>
    /// <param name="key">The form environment-variable name.</param>
    /// <param name="valid">Whether the existing form policy accepts this name.</param>
    [Theory]
    [InlineData("APP_NAME", true)]
    [InlineData("", true)]
    [InlineData("ConnectionStrings__Main", false)]
    [InlineData("1INVALID", false)]
    public void Existing_environment_constraints_are_preserved(string key, bool valid)
    {
        var config = new DeploymentConfigDto
        {
            Databases =
            [
                new DatabaseConfigDto
                {
                    DbType = "PostgreSQL", ConnectionStringName = "Main", DbName = "app",
                    DbUser = "appowner", DbPassword = "Synthetic8"
                }
            ]
        };

        Assert.Equal(valid, CloudRuntimeConfigurationValidation.Validate(config, [key]) is null);
        Assert.NotNull(CloudRuntimeConfigurationValidation.Validate(config, ["APP", "app"]));
    }
}
