namespace Infrastructure.Docker.Runtime;

/// <summary>
///     Configuration options for the Docker Service.
/// </summary>
public class DockerOptions
{
    /// <summary>Configuration section name used when binding Docker options.</summary>
    public const string SectionName = "Docker";

    /// <summary>Default container port used when callers do not provide a specific internal port.</summary>
    public int DefaultContainerPort { get; init; } = 8080;

    /// <summary>Maximum duration allowed for Docker Compose commands before they are cancelled.</summary>
    public int ComposeTimeoutMinutes { get; init; } = 8;

    /// <summary>Docker daemon URI used on Windows hosts.</summary>
    public string WindowsDockerUri { get; init; } = "npipe://./pipe/docker_engine";

    /// <summary>Docker daemon URI used on Unix-like hosts.</summary>
    public string UnixDockerUri { get; init; } = "unix:///var/run/docker.sock";

    /// <summary>Default build-context ignore patterns used when a project has no .dockerignore file.</summary>
    public string[] DefaultDockerIgnore { get; init; } =
    [
        "bin/", "obj/", ".git/", ".vs/", "node_modules/", "TestResults/", ".DS_Store"
    ];
}
