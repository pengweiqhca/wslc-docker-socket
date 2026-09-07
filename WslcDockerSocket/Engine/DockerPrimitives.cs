namespace WslcDockerSocket.Engine;

using System.Text;
using Api;

internal static class DockerEnvironment
{
    public static IDictionary<string, string> Parse(string[]? values)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
            {
                throw new DockerApiException(StatusCodes.Status400BadRequest,
                    $"Environment variable '{value}' must use KEY=value syntax.");
            }

            environment[value[..separator]] = value[(separator + 1)..];
        }

        return environment;
    }
}

internal static class DockerCommand
{
    public static IReadOnlyList<string> Combine(string[]? entrypoint, string[]? command)
    {
        var result = new List<string>();
        if (entrypoint is { Length: > 0 })
        {
            result.AddRange(entrypoint);
        }

        if (command is { Length: > 0 })
        {
            result.AddRange(command);
        }

        return result;
    }
}

internal static class DockerId
{
    public static string From(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
