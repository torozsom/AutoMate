using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Services.Scanner;

/// <summary>
///     Allocates available local ports for generated local deployment configurations.
/// </summary>
internal sealed class ScannerPortProvider(ILogger logger)
{
    /// <summary>
    ///     Default exposed port used when dynamic port allocation fails.
    /// </summary>
    private const int FallbackPort = 8080;

    /// <summary>
    ///     Allocates an available loopback TCP port and immediately releases it.
    /// </summary>
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