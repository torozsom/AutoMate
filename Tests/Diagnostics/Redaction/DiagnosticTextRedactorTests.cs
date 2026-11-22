using Infrastructure.Diagnostics.Redaction;
using Xunit;

namespace Tests.Diagnostics.Redaction;

/// <summary>
///     Verifies standalone credential masking, idempotence and text limits without the diagnostic pipeline.
/// </summary>
public sealed class DiagnosticTextRedactorTests
{
    /// <summary>
    ///     Supported secret formats are masked without erasing surrounding progress output.
    /// </summary>
    /// <param name="sensitive">Synthetic credential-bearing text that must be masked.</param>
    [Theory]
    [InlineData("{\"password\":\"private-value\"}")]
    [InlineData("{\"api-key\": \"private-value\"}")]
    [InlineData("{\"refresh_token\":\"private-value\"}")]
    [InlineData("{\"Authorization\":\"Bearer private-value\"}")]
    [InlineData("{\"Cookie\":\"session=private-value\"}")]
    [InlineData("DATABASE_PASSWORD='private-value with spaces'")]
    [InlineData("AWS_SECRET_ACCESS_KEY=private-value")]
    [InlineData("DB_PASS=private-value")]
    [InlineData("AccountKey=private-value")]
    [InlineData("https://example.invalid/path?sig=private-value&safe=yes")]
    [InlineData("passphrase=private-value")]
    [InlineData("ConnectionString=Server=host;Password=private-value;Database=sample")]
    [InlineData("Cookie: session=private-value; other=private-other")]
    [InlineData("Set-Cookie: session=private-value; HttpOnly")]
    [InlineData("Authorization: Basic private-value")]
    [InlineData("Bearer private-value")]
    [InlineData("https://user:private-value@example.invalid/path")]
    [InlineData("https://example.invalid/path?api_key=private-value&safe=yes")]
    [InlineData("github_pat_private123456789012345678901234567890")]
    [InlineData("ghp_private123456789012345678901234567890")]
    [InlineData("sk-proj-private123456789012345678901234567890")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nprivate-value\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----\\nprivate-value\\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nprivate-value")]
    [InlineData("password=\"private-value")]
    [InlineData("pass\u001b[31mword=private-value")]
    public void Masking_is_idempotent_across_supported_formats(string sensitive)
    {
        var redactor = new DiagnosticRedactor();
        var safe = redactor.RedactText("progress\r\n" + sensitive);

        Assert.StartsWith("progress\r\n", safe);
        Assert.DoesNotContain("private", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", safe);
        Assert.Equal(safe, redactor.RedactText(safe));
    }

    /// <summary>
    ///     Input caps fail closed, while ordinary long output stays bounded with a stable omission
    ///     marker.
    /// </summary>
    [Fact]
    public void Text_bounds_do_not_leak_or_change_on_repeated_redaction()
    {
        var redactor = new DiagnosticRedactor();

        Assert.Equal("[REDACTED]", redactor.RedactText(new string('x', 131_073) + "password=private-value"));

        var bounded = redactor.RedactText(new string('x', 8_000) + "password=private-value");

        Assert.Equal(4096, bounded.Length);
        Assert.Contains("[output truncated]", bounded);
        Assert.Equal(bounded, redactor.RedactText(bounded));
        Assert.Equal("blank\r\n\rprogress\n", redactor.RedactText("blank\r\n\rprogress\n"));
    }
}
