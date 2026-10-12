using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Scanner;

/// <summary>
///     Allocates available local ports for generated local deployment configurations.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class ScannerPortProvider(ILogger logger)
{
    /// <summary>Default exposed port used when dynamic port allocation fails.</summary>
    private const int FallbackPort = 8080;

    /// <summary>
    ///     Allocates an available loopback TCP port and immediately releases it.
    /// </summary>
    /// <returns>The numeric position, count or status described by this operation.</returns>
    public int GetAvailablePort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            logger.LogInformation("[ProjectScannerService] Successfully allocated dynamic port: {Port}", port);
            return port;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[ProjectScannerService] Failed to find an available port. Falling back to default port 8080.");
            return FallbackPort;
        }
    }
}