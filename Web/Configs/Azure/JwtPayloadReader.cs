using System.Text.Json;

namespace Web.Configs;

/// <summary>
///     Reads values from JWT payloads without validating the token.
/// </summary>
internal static class JwtPayloadReader
{
    /// <summary>
    ///     Extracts a string claim value from a JWT payload.
    /// </summary>
    /// <param name="jwt">JWT value to decode without signature or claim validation.</param>
    /// <param name="propertyName">JSON property or JWT claim name.</param>
    /// <returns>The string claim, or null for missing claims or malformed Base64/JSON; no JWT validation is performed.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The decoded payload is not an object or the selected claim is neither a string nor null.
    /// </exception>
    public static string? GetStringValue(string? jwt, string propertyName)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 })
            return null;

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}