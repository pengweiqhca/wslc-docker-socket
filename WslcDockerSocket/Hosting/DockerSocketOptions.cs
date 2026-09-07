namespace WslcDockerSocket.Hosting;

using System.Globalization;

internal sealed class DockerSocketOptions(ushort tcpPort, string namedPipe, bool disableNamedPipe, bool enableTcp)
{
    public ushort TcpPort { get; } = tcpPort;

    public string NamedPipe { get; } = namedPipe;

    public bool DisableNamedPipe { get; } = disableNamedPipe;

    public bool EnableTcp { get; } = enableTcp;

    public string TcpUrl => $"http://127.0.0.1:{TcpPort.ToString(CultureInfo.InvariantCulture)}";

    public static DockerSocketOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var tcpPort = configuration.GetValue<ushort?>("WSLC_DOCKER_SOCKET_TCP_PORT") ?? 23750;
        var namedPipe = configuration["WSLC_DOCKER_SOCKET_PIPE_NAME"] ?? "docker_engine";
        if (string.IsNullOrWhiteSpace(namedPipe))
        {
            throw new InvalidOperationException("WSLC_DOCKER_SOCKET_PIPE_NAME cannot be empty.");
        }

        var disableNamedPipe = configuration.GetValue<bool?>("WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE") ?? false;
        var enableTcp = configuration.GetValue<bool?>("WSLC_DOCKER_SOCKET_ENABLE_TCP") ?? false;
        if (disableNamedPipe && !enableTcp)
        {
            throw new InvalidOperationException(
                "At least one listener must be enabled. Set WSLC_DOCKER_SOCKET_ENABLE_TCP=true or enable the named pipe.");
        }

        return new DockerSocketOptions(tcpPort, namedPipe, disableNamedPipe, enableTcp);
    }
}
