using System.Text.Json;

namespace Services.Docker;

/// <summary>
///     Parses JSON output from <c>docker compose ls --format json</c>.
/// </summary>
internal static class DockerComposeProjectParser
{
    /// <summary>
    ///     Extracts Compose project names from Docker CLI JSON output.
    /// </summary>
    public static List<string> ParseRunningProjectNames(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var projects = JsonSerializer.Deserialize<JsonElement>(output);
        var runningProjects = new List<string>();

        if (projects.ValueKind == JsonValueKind.Array)
            foreach (var project in projects.EnumerateArray())
                if (project.TryGetProperty("Name", out var nameProp) && nameProp.GetString() is { } name)
                    runningProjects.Add(name);

        return runningProjects;
    }
}