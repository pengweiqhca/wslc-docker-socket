namespace WslcDockerSocket.Api;

using System.Text.Json;

internal static class DockerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };
}
