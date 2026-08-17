using System.Text.Json;
using Docker.DotNet.Models;

namespace Services.Docker;

/// <summary>
///     Creates Docker.DotNet container creation parameter objects from AutoMate deployment settings.
/// </summary>
internal static class DockerContainerParameters
{
    /// <summary>
    ///     Builds container creation parameters including image, name, environment variables, and port binding.
    /// </summary>
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