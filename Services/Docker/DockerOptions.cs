namespace Services.Docker;

/// <summary>
///     Configuration options for the Docker Service.
/// </summary>
public class DockerOptions
{
    public const string SectionName = "Docker";

    public int DefaultContainerPort { get; set; } = 8080;
    public int ComposeTimeoutMinutes { get; set; } = 8;
    public string WindowsDockerUri { get; set; } = "npipe://./pipe/docker_engine";
    public string UnixDockerUri { get; set; } = "unix:///var/run/docker.sock";

    public string[] DefaultDockerIgnore { get; set; } =
    [
        "bin/", "obj/", ".git/", ".vs/", "node_modules/", "TestResults/", ".DS_Store"
    ];
}