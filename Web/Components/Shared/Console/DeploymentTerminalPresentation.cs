using System.Globalization;
using System.Text;
using Application.Abstractions.Diagnostics;

namespace Web.Components.Shared;

/// <summary>
///     Formats Azure records and identifies stderr consistently in live and saved terminal output.
/// </summary>
public static class DeploymentTerminalPresentation
{
    /// <summary>
    ///     Renders Azure records as timestamped rows; database metric summaries render empty while
    ///     retaining replay identity.
    /// </summary>
    /// <param name="log">Source-aware redacted terminal record.</param>
    /// <returns>Formatted source output, or an empty string for suppressed database Metric records.</returns>
    public static string Format(DeploymentTerminalLog log)
    {
        if (log.TerminalChannel == "azure-db" && log.Stream == DeploymentDiagnosticStream.Metric)
            return string.Empty;

        if (log.TerminalChannel is "azure-web" or "azure-system" or "azure-db")
            return FormatAzure(log);

        return log.Stream == DeploymentDiagnosticStream.StandardError ? "[stderr] " + log.Message : log.Message;
    }

    /// <summary>
    ///     Prefixes each Azure message line with its recorded UTC time and stream, ending every row with
    ///     CRLF.
    /// </summary>
    /// <param name="log">Source-aware redacted terminal record.</param>
    /// <returns>Timestamped, stream-labelled Azure lines terminated with CRLF.</returns>
    private static string FormatAzure(DeploymentTerminalLog log)
    {
        var timestamp = log.TimestampUtc?.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture);
        var stream = log.Stream switch
        {
            DeploymentDiagnosticStream.StandardError => "STDERR",
            DeploymentDiagnosticStream.Control => "STATUS",
            DeploymentDiagnosticStream.System => "SYSTEM",
            DeploymentDiagnosticStream.Metric => "METRIC",
            _ => log.TerminalChannel == "azure-system" ? "SYSTEM" : "STDOUT"
        };
        var prefix = timestamp is null ? $"[{stream}] " : $"{timestamp} [{stream}] ";
        var output = new StringBuilder();
        using var reader = new StringReader(log.Message);
        while (reader.ReadLine() is { } line)
            output.Append(prefix).Append(line).Append("\r\n");

        return output.ToString();
    }
}
