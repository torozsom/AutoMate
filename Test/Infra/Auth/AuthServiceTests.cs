using Application.Abstractions.Email;
using Application.Auth;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Infra;

/// <summary>
///     Characterizes registration compensation, verification boundaries and local login at the real service boundary.
/// </summary>
public sealed class AuthServiceTests
{
    /// <summary>
    ///     Email failure removes the unverified account; successful delivery retains the original expiry and login rules.
    /// </summary>
    /// <param name="failEmail">Whether the synthetic transport rejects delivery.</param>
    /// <returns>Completes after registration and the applicable authentication assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_delivery_compensation_and_verification_boundary(bool failEmail)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoMateDbContext>().UseSqlite(connection).Options;
        await using var db = new AutoMateDbContext(options, new EphemeralDataProtectionProvider());
        await db.Database.EnsureCreatedAsync();
        var clock = new Clock();
        var email = new Email(db, failEmail);
        var auth = new AuthService(db, email, new PasswordHasher<LocalUser>(), NullLogger<AuthService>.Instance, clock);
        Assert.Equal(!failEmail, await auth.RegisterAsync("fixture", " OWNER@example.invalid ", "synthetic-password",
            token => "https://example.invalid/verify/" + token));
        Assert.Equal(1, email.AccountsDuringDelivery);
        if (failEmail)
        {
            Assert.Empty(await db.Users.ToListAsync());
            return;
        }

        var user = Assert.IsType<LocalUser>(await db.Users.SingleAsync());
        Assert.Equal("owner@example.invalid", user.Email);
        Assert.Equal(clock.Now.AddHours(24), user.VerificationTokenExpiry);
        Assert.Equal("Email not verified", (await auth.LoginAsync(user.Email, "synthetic-password")).ErrorMessage);
        Assert.Equal("Invalid credentials", (await auth.LoginAsync(user.Email, "wrong-password")).ErrorMessage);
        clock.Now = user.VerificationTokenExpiry!.Value;
        Assert.True(await auth.VerifyEmailAsync(user.EmailVerificationToken!));
        Assert.Null(user.EmailVerificationToken);
        Assert.Null(user.VerificationTokenExpiry);
        Assert.True(user.IsEmailVerified);
        Assert.Null((await auth.LoginAsync(user.Email, "synthetic-password")).ErrorMessage);
    }

    /// <summary>
    ///     Records persistence-before-delivery and optionally rejects transport without external SMTP traffic.
    /// </summary>
    /// <param name="db">The scenario's real metadata context.</param>
    /// <param name="fail">Whether delivery fails.</param>
    private sealed class Email(AutoMateDbContext db, bool fail) : IEmailSenderService
    {
        /// <summary>The number of persisted accounts when delivery started.</summary>
        public int AccountsDuringDelivery { get; private set; }

        /// <summary>
        ///     Observes the account before returning the selected transport outcome.
        /// </summary>
        /// <param name="toEmail">The normalized synthetic recipient.</param>
        /// <param name="subject">The existing verification subject.</param>
        /// <param name="message">The existing verification body.</param>
        /// <param name="cancellationToken">The unchanged operation token.</param>
        /// <returns>A completed send or the selected synthetic transport failure.</returns>
        public async Task SendEmailAsync(string toEmail, string subject, string message,
            CancellationToken cancellationToken = default)
        {
            AccountsDuringDelivery = await db.Users.CountAsync(cancellationToken);
            Assert.Contains("https://example.invalid/verify/", message);
            if (fail) throw new IOException("synthetic-delivery-failure");
        }
    }

    /// <summary>
    ///     Controls authentication expiry without delaying the test.
    /// </summary>
    private sealed class Clock : TimeProvider
    {
        /// <summary>The selected synthetic UTC instant.</summary>
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        ///     Returns the current synthetic time.
        /// </summary>
        /// <returns>The fixture UTC instant.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
