using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies canonical database aliases and preservation of unknown providers.
/// </summary>
public sealed class DatabaseTypeTests
{
    /// <summary>
    ///     Supported provider aliases resolve to the canonical names consumed by templates.
    /// </summary>
    /// <param name="input">Input value for the documented behavior.</param>
    /// <param name="expected">Expected result for this input.</param>
    [Theory]
    [InlineData("postgres", "PostgreSQL")]
    [InlineData(" PostgreSQL ", "PostgreSQL")]
    [InlineData("mysql", "MySQL")]
    [InlineData("sqlserver", "SQLServer")]
    [InlineData("sql-server", "SQLServer")]
    [InlineData("mssql", "SQLServer")]
    [InlineData("Microsoft SQL Server", "SQLServer")]
    [InlineData("mongo", "MongoDB")]
    [InlineData("mongodb", "MongoDB")]
    [InlineData("redis", "Redis")]
    public void NormalizeDatabaseType_SupportedAlias_ReturnsCanonicalName(string input, string expected)
    {
        var actual = TemplateNameNormalizer.NormalizeDatabaseType(input);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     Unknown provider names are trimmed without inventing a provider mapping or changing casing.
    /// </summary>
    [Fact]
    public void NormalizeDatabaseType_UnknownProvider_ReturnsTrimmedOriginalName()
    {
        var actual = TemplateNameNormalizer.NormalizeDatabaseType("  CustomDB  ");

        Assert.Equal("CustomDB", actual);
    }
}
