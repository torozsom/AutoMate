using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Policy;

/// <summary>
///     Provides safe, setting-specific startup errors for incomplete telemetry configuration.
/// </summary>
public sealed class TelemetryStorageOptionsValidator : IValidateOptions<TelemetryStorageOptions>
{
    /// <summary>
    ///     Validates configured storage bounds and reports fixed setting-specific guidance.
    /// </summary>
    /// <param name="name">The recorded display name or fixed metric identity.</param>
    /// <param name="options">The current validated options used by this operation.</param>
    /// <returns>Success or fixed setting-specific guidance for invalid option bounds.</returns>
    public ValidateOptionsResult Validate(string? name, TelemetryStorageOptions options)
    {
        var errors = options.GetValidationErrors().ToArray();
        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
