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