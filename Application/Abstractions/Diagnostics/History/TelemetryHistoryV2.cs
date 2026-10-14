using System.Text;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;

namespace Application.Abstractions.Diagnostics.History;

/// <summary>
///     Versioned terminal history with resource-scoped navigation cursors and storage
///     availability.
/// </summary>
/// <param name="Events">The bounded batch of redacted diagnostic envelopes.</param>
/// <param name="EarlierCursor">
///     The resource-scoped cursor for retrieving earlier history, if available.
/// </param>
/// <param name="LaterCursor">The resource-scoped cursor for retrieving later history, if available.</param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
/// <param name="CanAdvanceCursor">
///     Whether storage confirmation permits the caller to advance its replay cursor.
/// </param>
public sealed record TelemetryLogPage(
    IReadOnlyList<DeploymentTerminalLog> Events,
    string? EarlierCursor,
    string? LaterCursor,
    string? Availability,
    bool CanAdvanceCursor);

/// <summary>
///     Opaque, versioned, resource-scoped position; it is not an authorization token.
/// </summary>
public static class TelemetryHistoryCursor
{
    /// <summary>
    ///     Encodes a versioned log position scoped to a project and deployment.
    /// </summary>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="order">The durable log position encoded in the cursor.</param>
    /// <returns>The Base64 version-two project/deployment cursor.</returns>
    public static string Encode(Guid project, Guid deployment, long order)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"2:{project:N}:{deployment:N}:{order}"));
    }

    /// <summary>
    ///     Validates and decodes a resource-scoped history position; absent cursors start at zero.
    /// </summary>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <returns>The positive stored ordering position, or zero for an absent cursor.</returns>
    /// <exception cref="ArgumentException">
    ///     The cursor is malformed, oversized, nonpositive, or belongs to a different resource.
    /// </exception>
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

    /// <summary>
    ///     Projects terminal history into a page with opaque backward and forward cursors.
    /// </summary>
    /// <param name="history">The bounded terminal history and its coverage metadata.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <returns>Terminal records and resource-scoped navigation cursors preserving coverage flags.</returns>
    public static TelemetryLogPage Page(DeploymentTerminalHistory history, Guid project, Guid deployment)
    {
        return new TelemetryLogPage(history.Events, history.EarlierOmitted && history.Events.Count > 0
                ? Encode(project, deployment, history.Events[0].OrderId)
                : null,
            history.Events.Count > 0 ? Encode(project, deployment, history.Events[^1].OrderId) : null,
            history.Availability, history.CanAdvanceCursor);
    }
}

/// <summary>
///     Reads bounded, resource-scoped log pages with plain-text search.
/// </summary>
public interface IDeploymentLogSearch
{
    /// <summary>
    ///     Reads a bounded ordered log page matching plain-text search within one resource scope.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning a bounded resource-scoped page of matching log envelopes.</returns>
    Task<IReadOnlyList<DeploymentLogEnvelope>> SearchAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string search, CancellationToken token);
}
