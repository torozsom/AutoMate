using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Test.Infra.Data;

/// <summary>
///     Verifies the extracted mappings still match the checked-in PostgreSQL model without contacting a database.
/// </summary>
public sealed class PersistenceModelTests
{
    /// <summary>
    ///     Mapping extraction and clock seams introduce no pending persistence-model change.
    /// </summary>
    [Fact]
    public void Extracted_model_matches_the_existing_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<AutoMateDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_fixture")
            .UseSnakeCaseNamingConvention().Options;
        using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
