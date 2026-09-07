namespace WslcDockerSocket.Engine;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Api;
using Api.Contracts;
using Microsoft.WSL.Containers;

internal readonly record struct DockerPortKey(ushort ContainerPort, PortProtocol Protocol)
{
    public override string ToString() => ContainerPort.ToString(CultureInfo.InvariantCulture) + "/"
        + (Protocol == PortProtocol.TCP ? "tcp" : "udp");
}

internal readonly record struct DockerPortBinding(ushort ContainerPort, ushort? HostPort, PortProtocol Protocol)
{
    public DockerPortKey Key => new(ContainerPort, Protocol);

    public ContainerPortMapping ToWslc() => new(HostPort ?? 0, ContainerPort, Protocol);

    public static IReadOnlyList<DockerPortBinding> Parse(Dictionary<string, List<DockerHostPortBinding>?>? bindings)
    {
        var result = new List<DockerPortBinding>();
        foreach (var (portAndProtocol, mapping) in bindings ?? [])
        {
            var separator = portAndProtocol.IndexOf('/');
            var portText = separator < 0 ? portAndProtocol : portAndProtocol[..separator];
            if (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort)
                || containerPort == 0)
            {
                throw new DockerApiException(StatusCodes.Status400BadRequest,
                    $"Invalid container port binding '{portAndProtocol}'.");
            }

            var protocol = ParseProtocol(separator < 0 ? null : portAndProtocol[(separator + 1)..], portAndProtocol);
            if (mapping is { Count: > 1 })
            {
                throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    "Multiple host bindings for one container port are not supported by WSLC.");
            }

            var first = mapping?.FirstOrDefault();
            if (first?.HostIp is { Length: > 0 } hostIp && hostIp != "0.0.0.0")
            {
                throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    "Binding a port to a specific or IPv6 host IP address is not supported by WSLC.");
            }

            ushort? hostPort = null;
            if (!string.IsNullOrWhiteSpace(first?.HostPort) && first.HostPort != "0")
            {
                if (!ushort.TryParse(first.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedHostPort)
                    || parsedHostPort == 0)
                {
                    throw new DockerApiException(StatusCodes.Status400BadRequest,
                        $"Invalid host port '{first.HostPort}'.");
                }

                hostPort = parsedHostPort;
            }

            result.Add(new DockerPortBinding(containerPort, hostPort, protocol));
        }

        return result;
    }

    public static IReadOnlyDictionary<string, object?> ToDockerResponse(
        IReadOnlyList<DockerPortBinding> requested,
        IReadOnlyDictionary<DockerPortKey, ushort> resolved)
    {
        var result = new Dictionary<string, object?>();
        foreach (var binding in requested)
        {
            result[binding.Key.ToString()] = resolved.TryGetValue(binding.Key, out var hostPort)
                ? new[] { new { HostIp = "0.0.0.0", HostPort = hostPort.ToString(CultureInfo.InvariantCulture) } }
                : null;
        }

        return new ReadOnlyDictionary<string, object?>(result);
    }

    public static IEnumerable<object> ToListResponse(IReadOnlyList<DockerPortBinding> requested,
        IReadOnlyDictionary<DockerPortKey, ushort> resolved) => [.. requested
        .Where(binding => resolved.ContainsKey(binding.Key))
        .Select(binding => new
        {
            PrivatePort = binding.ContainerPort,
            PublicPort = resolved[binding.Key],
            Type = binding.Protocol == PortProtocol.TCP ? "tcp" : "udp",
            IP = "0.0.0.0",
        })
        .Cast<object>()];

    public static IReadOnlyDictionary<DockerPortKey, ushort> ParseRuntimeInspect(string inspectJson)
    {
        var result = new Dictionary<DockerPortKey, ushort>();
        using var document = JsonDocument.Parse(inspectJson);
        Collect(document.RootElement, result);
        return result;
    }

    private static void Collect(JsonElement element, IDictionary<DockerPortKey, ushort> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPortPair(element, out var key, out var hostPort))
            {
                result[key] = hostPort;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryReadPortName(property.Name, out key) && TryReadHostPort(property.Value, out hostPort))
                {
                    result[key] = hostPort;
                }

                Collect(property.Value, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                Collect(child, result);
            }
        }
    }

    private static bool TryGetPortPair(JsonElement element, out DockerPortKey key, out ushort hostPort)
    {
        key = default;
        hostPort = 0;
        if (!TryGetProperty(element, "ContainerPort", out var container)
            || !TryGetProperty(element, "HostPort", out var host)
            || !TryReadPort(container, out var containerPort)
            || !TryReadPort(host, out hostPort))
        {
            return false;
        }

        var protocol = PortProtocol.TCP;
        if (TryGetProperty(element, "Protocol", out var protocolElement)
            && protocolElement.ValueKind == JsonValueKind.String)
        {
            protocol = ParseRuntimeProtocol(protocolElement.GetString());
        }

        key = new DockerPortKey(containerPort, protocol);
        return true;
    }

    private static bool TryReadPortName(string name, out DockerPortKey key)
    {
        var separator = name.IndexOf('/');
        if (!ushort.TryParse(separator < 0 ? name : name[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port == 0)
        {
            key = default;
            return false;
        }

        key = new DockerPortKey(port, separator < 0 ? PortProtocol.TCP : ParseRuntimeProtocol(name[(separator + 1)..]));
        return true;
    }

    private static bool TryReadHostPort(JsonElement element, out ushort port)
    {
        if (TryReadPort(element, out port))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (TryReadHostPort(child, out port))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, "HostPort", out var host) && TryReadHostPort(host, out port))
            {
                return true;
            }

            foreach (var child in element.EnumerateObject())
            {
                if (TryReadHostPort(child.Value, out port))
                {
                    return true;
                }
            }
        }

        port = 0;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadPort(JsonElement element, out ushort port)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt16(out port) && port > 0)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && ushort.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port > 0)
        {
            return true;
        }

        port = 0;
        return false;
    }

    private static PortProtocol ParseProtocol(string? protocol, string portAndProtocol)
    {
        return protocol is null or "" || protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            ? PortProtocol.TCP
            : protocol.Equals("udp", StringComparison.OrdinalIgnoreCase)
                ? PortProtocol.UDP
                : throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    $"Port protocol in binding '{portAndProtocol}' is not supported.");
    }

    private static PortProtocol ParseRuntimeProtocol(string? protocol) =>
        protocol?.Equals("udp", StringComparison.OrdinalIgnoreCase) == true ? PortProtocol.UDP : PortProtocol.TCP;
}
