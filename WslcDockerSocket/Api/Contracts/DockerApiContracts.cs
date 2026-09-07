namespace WslcDockerSocket.Api.Contracts;

internal sealed class DockerCreateContainerRequest
{
    public string? Hostname { get; set; }

    public string? Image { get; set; }

    public string[]? Cmd { get; set; }

    public string[]? Entrypoint { get; set; }

    public string[]? Env { get; set; }

    public string? WorkingDir { get; set; }

    public bool Tty { get; set; }

    public Dictionary<string, string>? Labels { get; set; }

    public Dictionary<string, object>? ExposedPorts { get; set; }

    public DockerHostConfig? HostConfig { get; set; }

    public DockerNetworkingConfig? NetworkingConfig { get; set; }
}

internal sealed class DockerHostConfig
{
    public bool AutoRemove { get; set; }

    public bool Privileged { get; set; }

    public string? NetworkMode { get; set; }

    public Dictionary<string, List<DockerHostPortBinding>?>? PortBindings { get; set; }

    public string[]? Binds { get; set; }

    public List<object>? Mounts { get; set; }

    public Dictionary<string, string>? Tmpfs { get; set; }
}

internal sealed class DockerHostPortBinding
{
    public string? HostIp { get; set; }

    public string? HostPort { get; set; }
}

internal sealed class DockerNetworkingConfig
{
    public Dictionary<string, object>? EndpointsConfig { get; set; }
}

internal sealed class DockerExecCreateRequest
{
    public string[]? Cmd { get; set; }

    public string[]? Env { get; set; }

    public string? WorkingDir { get; set; }
}
