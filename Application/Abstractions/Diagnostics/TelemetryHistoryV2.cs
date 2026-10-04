using System.Text;

namespace Application.Abstractions.Diagnostics;

public sealed record TelemetryLogPage(
    IReadOnlyList<DeploymentTerminalLog> Events,
    string? EarlierCursor,
    string? LaterCursor,
    string? Availability,
    bool CanAdvanceCursor);

/// <summary>Opaque, versioned, resource-scoped position; it is not an authorization token.</summary>
public static class TelemetryHistoryCursor
{
    public static string Encode(Guid project, Guid deployment, long order)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"2:{project:N}:{deployment:N}:{order}"));
    }

    public static long Decode(string? cursor, Guid project, Guid deployment)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        if (cursor.Length > 256) throw new ArgumentException("Invalid history cursor.");
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length == 4 && parts[0] == "2" && parts[1] == project.ToString("N") &&
                parts[2] == deployment.ToString("N") && long.TryParse(parts[3], out var order) &&
                order > 0) return order;
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Invalid or mismatched history cursor.");
    }

    public static TelemetryLogPage Page(DeploymentTerminalHistory history, Guid project, Guid deployment)
    {
        return new TelemetryLogPage(history.Events, history.EarlierOmitted && history.Events.Count > 0
                ? Encode(project, deployment, history.Events[0].OrderId)
                : null,
            history.Events.Count > 0 ? Encode(project, deployment, history.Events[^1].OrderId) : null,
            history.Availability, history.CanAdvanceCursor);
    }
}

public interface IDeploymentLogSearch
{
    Task<IReadOnlyList<DeploymentLogEnvelope>> SearchAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string search, CancellationToken token);
}