namespace WslcDockerSocket.Hosting;

using System.Net;

internal sealed class DockerSocketOptions(ushort tcpPort, string namedPipe, bool disableNamedPipe, bool enableTcp,
    bool disableHyperVTcp, string? hyperVTcpAddress)
{
    public ushort TcpPort { get; } = tcpPort;

    public string NamedPipe { get; } = namedPipe;

    public bool DisableNamedPipe { get; } = disableNamedPipe;

    public bool EnableTcp { get; } = enableTcp;

    /// <summary>Whether the additional listener on the Hyper-V virtual switch adapter address is turned off.</summary>
    public bool DisableHyperVTcp { get; } = disableHyperVTcp;

    /// <summary>Overrides auto-detection of the Hyper-V virtual switch adapter address.</summary>
    public string? HyperVTcpAddress { get; } = hyperVTcpAddress;

    public static DockerSocketOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var tcpPort = configuration.GetValue<ushort?>("WSLC_DOCKER_SOCKET_TCP_PORT") ?? 2375;
        var namedPipe = configuration["WSLC_DOCKER_SOCKET_PIPE_NAME"] ?? "docker_engine";
        if (string.IsNullOrWhiteSpace(namedPipe))
        {
            throw new InvalidOperationException("WSLC_DOCKER_SOCKET_PIPE_NAME cannot be empty.");
        }

        var disableNamedPipe = configuration.GetValue<bool?>("WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE") ?? false;
        var enableTcp = configuration.GetValue<bool?>("WSLC_DOCKER_SOCKET_ENABLE_TCP") ?? false;
        var disableHyperVTcp = configuration.GetValue<bool?>("WSLC_DOCKER_SOCKET_DISABLE_HYPERV_TCP") ?? false;
        var configuredHyperVTcpAddress = configuration["WSLC_DOCKER_SOCKET_HYPERV_TCP_ADDRESS"];
        var hyperVTcpAddress = configuredHyperVTcpAddress;
        if (!disableHyperVTcp && string.IsNullOrWhiteSpace(hyperVTcpAddress))
        {
            hyperVTcpAddress = HyperVNetwork.GetHostVirtualNics().SelectMany(x => x.IPv4Addresses).FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(hyperVTcpAddress) && !IPAddress.TryParse(hyperVTcpAddress, out _))
        {
            throw new InvalidOperationException("WSLC_DOCKER_SOCKET_HYPERV_TCP_ADDRESS must be a valid IPv4 or IPv6 address.");
        }

        if (!disableHyperVTcp && string.IsNullOrWhiteSpace(hyperVTcpAddress))
        {
            throw new InvalidOperationException(
                "Could not detect the Hyper-V virtual switch adapter's IP address. Set WSLC_DOCKER_SOCKET_HYPERV_TCP_ADDRESS to override, or WSLC_DOCKER_SOCKET_DISABLE_HYPERV_TCP=true to turn the listener off.");
        }

        if (disableNamedPipe && !enableTcp && disableHyperVTcp)
        {
            throw new InvalidOperationException(
                "At least one listener must be enabled. Set WSLC_DOCKER_SOCKET_ENABLE_TCP=true, WSLC_DOCKER_SOCKET_DISABLE_HYPERV_TCP=false, or enable the named pipe.");
        }

        return new DockerSocketOptions(tcpPort, namedPipe, disableNamedPipe, enableTcp, disableHyperVTcp, hyperVTcpAddress);
    }
}
