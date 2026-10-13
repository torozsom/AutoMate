using System.Net;
using System.Net.Sockets;
using Application.Orchestration;
using FluentAssertions;

namespace Test.Infra.Orchestration;

/// <summary>
///     Verifies bound-port detection before local Docker deployment starts.
/// </summary>
public sealed class LocalHostPortAvailabilityTests
{
    /// <summary>
    ///     Verifies that reports a bound host port before Docker is started.
    /// </summary>
    [Fact]
    public void Reports_a_bound_host_port_before_docker_is_started()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var check = () => LocalHostPortAvailability.Check(port);
            check.Should().Throw<DeploymentResourceConflictException>()
                .WithMessage($"Host port {port} is already in use*");
        }
        finally
        {
            listener.Stop();
        }
    }
}
