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

internal sealed class DockerCreateNetworkRequest
{
    public string? Name { get; set; }

    public string? Driver { get; set; }

    public bool Internal { get; set; }

    public bool Attachable { get; set; }

    public bool EnableIPv6 { get; set; }

    public DockerNetworkIpam? IPAM { get; set; }

    public Dictionary<string, string>? Options { get; set; }

    public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerNetworkIpam
{
    public string? Driver { get; set; }

    public List<DockerNetworkIpamConfig>? Config { get; set; }
}

internal sealed class DockerNetworkIpamConfig
{
    public string? Subnet { get; set; }

    public string? Gateway { get; set; }

    public string? IPRange { get; set; }
}

internal sealed class DockerNetworkConnectRequest
{
    public string? Container { get; set; }

    public DockerNetworkEndpointConfig? EndpointConfig { get; set; }
}

internal sealed class DockerNetworkEndpointConfig
{
    public string[]? Aliases { get; set; }

    public string[]? Links { get; set; }

    public DockerNetworkEndpointIpamConfig? IPAMConfig { get; set; }
}

internal sealed class DockerNetworkEndpointIpamConfig
{
    public string? IPv4Address { get; set; }

    public string? IPv6Address { get; set; }
}

internal sealed class DockerNetworkDisconnectRequest
{
    public string? Container { get; set; }

    public bool Force { get; set; }
}

internal sealed class DockerExecCreateRequest
{
    public string[]? Cmd { get; set; }

    public string[]? Env { get; set; }

    public string? WorkingDir { get; set; }
}
