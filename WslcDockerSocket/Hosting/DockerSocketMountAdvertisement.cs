namespace WslcDockerSocket.Hosting;

/// <summary>
/// The Docker endpoint advertised to containers that request a Docker-socket bind mount (e.g. Testcontainers'
/// Ryuk), in place of a real bind mount WSLC cannot provide. Populated only when the WSL virtual switch TCP
/// listener is enabled, since containers can reach that address but never a path on the Windows filesystem.
/// </summary>
internal sealed record DockerSocketMountAdvertisement(Uri? DockerHost)
{
    public static readonly DockerSocketMountAdvertisement None = new((Uri?)null);
}
