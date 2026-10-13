using System.Text.Json;
using Docker.DotNet.Models;

namespace Infrastructure.Docker;

/// <summary>
///     Creates Docker.DotNet container creation parameter objects from AutoMate deployment
///     settings.
/// </summary>
internal static class DockerContainerParameters
{
    /// <summary>
    ///     Builds container creation parameters including image, name, environment variables, and
    ///     port binding.
    /// </summary>
    /// <param name="imageTag">Docker image tag used for the selected deployment.</param>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="hostPort">The port exposed on the Docker host.</param>
    /// <param name="containerPort">The application port inside the container.</param>
    /// <param name="envVarsJson">The JSON environment-variable map supplied to the container.</param>
    /// <returns>The create container parameters projection derived from the verified inputs.</returns>
    public static CreateContainerParameters Create(
        string imageTag,
        string containerName,
        int hostPort,
        int containerPort,
        string? envVarsJson)
    {
        var envList = CreateEnvironmentList(envVarsJson);

        return new CreateContainerParameters
        {
            Image = imageTag,
            Name = containerName,
            Env = envList,
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                { $"{containerPort}/tcp", default }
            },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{containerPort}/tcp",
                        [new PortBinding { HostPort = hostPort.ToString() }]
                    }
                }
            }
        };
    }

    /// <summary>
    ///     Converts persisted JSON environment variables into Docker's KEY=VALUE format.
    /// </summary>
    /// <param name="envVarsJson">The JSON environment-variable map supplied to the container.</param>
    /// <returns>The environment list selection with its documented bounds and ordering.</returns>
    private static List<string> CreateEnvironmentList(string? envVarsJson)
    {
        var envList = new List<string>();
        if (string.IsNullOrWhiteSpace(envVarsJson))
            return envList;

        var envDict = JsonSerializer.Deserialize<Dictionary<string, string>>(envVarsJson);
        if (envDict != null)
            envList.AddRange(envDict.Select(kv => $"{kv.Key}={kv.Value}"));

        return envList;
    }
}
