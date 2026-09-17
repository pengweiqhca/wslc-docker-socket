using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WslcDockerSocket.Hosting;

public sealed class HyperVHostNic
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string DeviceId { get; init; }
    public required string MacAddress { get; init; }
    public required IReadOnlyList<string> IPv4Addresses { get; init; }
}

public static class HyperVNetwork
{
    private const string Namespace = @"\\.\root\virtualization\v2";
    private static readonly string[] VirtualAdapterHints = ["WSL", "Default Switch"];

    public static IEnumerable<HyperVHostNic> GetHostVirtualNics()
    {
        IEnumerable<HyperVHostNic> wmiNics = [];
        try
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM Msvm_InternalEthernetPort"));

            using var ports = searcher.Get();
            wmiNics = [.. ReadWmiNics(ports)];
        }
        catch (ManagementException)
        {
        }

        foreach (var nic in wmiNics)
        {
            yield return nic;
        }

        if (!wmiNics.Any())
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up)
                .Where(x => VirtualAdapterHints.Any(hint =>
                    x.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || x.Description.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new HyperVHostNic
                {
                    Name = x.Name,
                    Description = x.Description,
                    DeviceId = x.Id,
                    MacAddress = x.GetPhysicalAddress().ToString(),
                    IPv4Addresses =
                    [
                        .. x.GetIPProperties().UnicastAddresses
                            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(address => address.Address.ToString())
                    ]
                }))
            {
                yield return nic;
            }
        }
    }

    private static IEnumerable<HyperVHostNic> ReadWmiNics(ManagementObjectCollection ports)
    {
        foreach (var port in ports.Cast<ManagementObject>())
        {
            var deviceId = port["DeviceID"]?.ToString();
            if (string.IsNullOrWhiteSpace(deviceId)) continue;

            var mac = port["PermanentAddress"]?.ToString();
            var nic = FindNetworkInterface(mac);
            if (nic is null) continue;

            yield return new HyperVHostNic
            {
                Name = nic.Name,
                Description = nic.Description,
                DeviceId = deviceId,
                MacAddress = mac ?? nic.GetPhysicalAddress().ToString(),
                IPv4Addresses =
                [
                    .. nic.GetIPProperties().UnicastAddresses
                        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(address => address.Address.ToString())
                ]
            };
        }
    }

    private static NetworkInterface? FindNetworkInterface(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;

        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var normalizedMac = NormalizeMac(mac);

        return interfaces.FirstOrDefault(x =>
            x.OperationalStatus == OperationalStatus.Up &&
            NormalizeMac(x.GetPhysicalAddress().ToString()) == normalizedMac);
    }

    private static string NormalizeMac(string mac)
    {
        return new string(
            [.. mac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant)]);
    }
}
