using System.Text.Json;
using WslcDockerSocket.Api.Contracts;

namespace WslcDockerSocket.Engine;

/// <summary>
/// Recognizes a request whose entire mount configuration is a single read-only bind of the Docker socket path
/// (what Testcontainers' Ryuk and similar reaper containers ask for). WSLC has no socket file to back that mount,
/// but a container asking for it only ever wants to reach a Docker Engine API, so the request can be honored by
/// pointing the container at this engine over TCP instead of performing a mount.
/// </summary>
internal static class DockerSocketMount
{
    private const string DockerSocketPath = "/var/run/docker.sock";

    public static bool IsSoleDockerSocketBind(DockerHostConfig? hostConfig)
    {
        if (hostConfig?.Tmpfs is { Count: > 0 })
        {
            return false;
        }

        var binds = hostConfig?.Binds ?? [];
        var mounts = hostConfig?.Mounts ?? [];
        if (binds.Length + mounts.Count != 1)
        {
            return false;
        }

        return binds.Length == 1 ? IsDockerSocketBindString(binds[0]) : IsDockerSocketMountEntry(mounts[0]);
    }

    private static bool IsDockerSocketBindString(string bind)
    {
        var parts = bind.Split(':');
        return parts is [_, DockerSocketPath, ..];
    }

    private static bool IsDockerSocketMountEntry(object mount)
    {
        if (mount is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return false;
        }

        var type = ReadString(element, "Type");
        return (type is null or "" || type.Equals("bind", StringComparison.OrdinalIgnoreCase))
            && ReadString(element, "Target") == DockerSocketPath;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
