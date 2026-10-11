using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Provides safe, setting-specific startup errors for incomplete telemetry configuration.</summary>
public sealed class TelemetryStorageOptionsValidator : IValidateOptions<TelemetryStorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TelemetryStorageOptions options)
    {
        var errors = options.GetValidationErrors().ToArray();
        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}