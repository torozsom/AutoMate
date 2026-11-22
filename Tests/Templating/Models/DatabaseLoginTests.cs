using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies which canonical database providers require login parameters.
/// </summary>
public sealed class DatabaseLoginTests
{
    /// <summary>
    ///     Only the canonical relational providers require generated username and password parameters.
    /// </summary>
    /// <param name="provider">Canonical database provider name.</param>
    /// <param name="expected">Expected result for this input.</param>
    [Theory]
    [InlineData("PostgreSQL", true)]
    [InlineData("MySQL", true)]
    [InlineData("SQLServer", true)]
    [InlineData("MongoDB", false)]
    [InlineData("Redis", false)]
    public void RequiresDatabaseLogin_CanonicalProvider_ReturnsLoginRequirement(string provider, bool expected)
    {
        var actual = TemplateNameNormalizer.RequiresDatabaseLogin(provider);

        Assert.Equal(expected, actual);
    }
}
