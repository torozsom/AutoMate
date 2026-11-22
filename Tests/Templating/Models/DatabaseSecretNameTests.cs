using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies indexed repository-secret names for generated database credentials.
/// </summary>
public sealed class DatabaseSecretNameTests
{
    /// <summary>
    ///     The database index identifies the username secret independently of credential values.
    /// </summary>
    [Fact]
    public void GetDatabaseUsernameSecretName_DatabaseIndex_ReturnsIndexedUsernameName()
    {
        var actual = CloudDeploymentSecretNames.GetDatabaseUsernameSecretName(2);

        Assert.Equal("AUTOMATE_DB_2_USERNAME", actual);
    }

    /// <summary>
    ///     Password secret names use the same database index and their own credential suffix.
    /// </summary>
    [Fact]
    public void GetDatabasePasswordSecretName_DatabaseIndex_ReturnsIndexedPasswordName()
    {
        var actual = CloudDeploymentSecretNames.GetDatabasePasswordSecretName(2);

        Assert.Equal("AUTOMATE_DB_2_PASSWORD", actual);
    }
}
