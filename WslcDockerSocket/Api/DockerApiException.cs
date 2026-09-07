namespace WslcDockerSocket.Api;

internal sealed class DockerApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
