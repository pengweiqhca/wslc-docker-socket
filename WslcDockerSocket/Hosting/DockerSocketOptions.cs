using System.Security.Principal;

namespace WslcDockerSocket.Hosting;

using System.Net;

internal sealed class DockerSocketOptions
{
    /// <summary>
    /// The WSLC session every command runs in. It defaults to the session name wslc derives for the current account, so an elevated adapter stays on the same session as an unelevated one instead of getting the separate <c>wslc-cli-admin-*</c> session wslc picks for an elevated token. Set it empty to let wslc choose the session for the current process.
    /// </summary>
    public string? Session { get; init; }

    public required ushort TcpPort { get; init; }

    public required string NamedPipe { get; init; }

    public required bool DisableNamedPipe { get; init; }

    public required bool EnableTcp { get; init; }

    /// <summary>Whether the additional listener on the Hyper-V virtual switch adapter address is turned off.</summary>
    public required bool DisableHyperVTcp { get; init; }

    /// <summary>Overrides auto-detection of the Hyper-V virtual switch adapter address.</summary>
    public string? HyperVTcpAddress { get; init; }

    public static DockerSocketOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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

        return new DockerSocketOptions
        {
            NamedPipe = namedPipe,
            DisableNamedPipe = disableNamedPipe,
            TcpPort = configuration.GetValue<ushort?>("WSLC_DOCKER_SOCKET_TCP_PORT") ?? 2375,
            EnableTcp = enableTcp,
            DisableHyperVTcp = disableHyperVTcp,
            HyperVTcpAddress = hyperVTcpAddress,
            Session = IsAdministrator() ? $"wslc-cli-{Environment.UserName}" : null,
        };
    }

    /// <summary>
    /// 判断当前进程是否以管理员身份运行
    /// </summary>
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
