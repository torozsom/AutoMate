using System.Text.Json;

namespace Services.Scanner;

/// <summary>
///     Flattens JSON configuration into double-underscore separated keys.
/// </summary>
internal static class JsonConfigurationFlattener
{
    /// <summary>
    ///     Recursively flattens objects and arrays into the provided result dictionary.
    /// </summary>
    public static void Flatten(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var newKey = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}__{property.Name}";
                    Flatten(property.Value, newKey, result);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var newKey = $"{prefix}__{index}";
                    Flatten(item, newKey, result);
                    index++;
                }

                break;

            default:
                result[prefix] = element.ToString();
                break;
        }
    }
}