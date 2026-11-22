using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies collision resistance, determinism and length bounds for custom environment-secret names.
/// </summary>
public sealed class EnvironmentSecretNameTests
{
    /// <summary>
    ///     Different keys that normalize to the same segment retain distinct, repeatable secret identities.
    /// </summary>
    [Fact]
    public void GetCustomEnvironmentSecretName_NormalizationCollision_ReturnsDistinctStableNames()
    {
        var first = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(0, "APP-KEY");
        var second = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(0, "APP_KEY");
        var repeated = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(0, "APP-KEY");

        Assert.StartsWith("AUTOMATE_ENV_0_APP_KEY_", first);
        Assert.StartsWith("AUTOMATE_ENV_0_APP_KEY_", second);
        Assert.NotEqual(first, second);
        Assert.Equal(first, repeated);
        Assert.Matches("^AUTOMATE_ENV_0_APP_KEY_[0-9A-F]{8}$", first);
    }

    /// <summary>
    ///     Truncating a long key retains its disambiguating hash within the 100-character limit.
    /// </summary>
    [Fact]
    public void GetCustomEnvironmentSecretName_LongKey_RetainsHashWithinLengthLimit()
    {
        var key = new string('a', 150);

        var actual = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(1, key);
        var other = CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(1, key + "b");

        Assert.Equal(100, actual.Length);
        Assert.StartsWith("AUTOMATE_ENV_1_", actual);
        Assert.Matches("^[A-Z0-9_]+_[0-9A-F]{8}$", actual);
        Assert.NotEqual(actual, other);
    }
}
