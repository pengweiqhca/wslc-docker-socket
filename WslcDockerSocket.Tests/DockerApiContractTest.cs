using System.Net;
using System.Net.Http.Json;

namespace WslcDockerSocket.Tests;

public sealed class DockerApiContractTest(IHttpClientFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient("docker");

    [Fact]
    public async Task VersionedPingAndVersionExposeDockerCompatibleValues()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ping = await _client.GetAsync("/v1.43/_ping", ct);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal("OK", await ping.Content.ReadAsStringAsync(ct));

        using var version = await _client.GetAsync("/v1.43/version", ct);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        using var document = JsonDocument.Parse(await version.Content.ReadAsStringAsync(ct));
        Assert.Equal("1.43", document.RootElement.GetProperty("ApiVersion").GetString());
        Assert.Equal("linux", document.RootElement.GetProperty("Os").GetString());
        Assert.NotEqual("26.1.0", document.RootElement.GetProperty("Version").GetString());
        Assert.False(document.RootElement.TryGetProperty("GoVersion", out _));
        Assert.False(document.RootElement.TryGetProperty("KernelVersion", out _));
    }

    [Fact]
    public async Task CreateWithoutImageReturnsDockerBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.PostAsync("/v1.43/containers/create", JsonContent.Create(new { }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDockerErrorAsync(response, "Image is required", ct);
    }

    [Theory]
    [InlineData("Binds", "Bind mounts")]
    [InlineData("NetworkMode", "default bridge network")]
    [InlineData("Tty", "TTY containers")]
    public async Task CreateWithUnsupportedOptionFailsFastInsteadOfClaimingSupport(string option, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        object body = option switch
        {
            "Binds" => new { Image = "alpine:3.20.3", HostConfig = new { Binds = new[] { "C:/host:/container" } } },
            "NetworkMode" => new { Image = "alpine:3.20.3", HostConfig = new { NetworkMode = "host" } },
            "Tty" => new { Image = "alpine:3.20.3", Tty = true },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null),
        };
        using var response = await _client.PostAsync("/v1.43/containers/create", JsonContent.Create(body), ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, message, ct);
    }

    [Theory]
    [InlineData("/v1.43/networks/not-created", "No such network: not-created")]
    [InlineData("/v1.43/volumes/not-created", "No such volume: not-created")]
    [InlineData("/v1.43/images/not-created", "No such image: not-created")]
    public async Task DeletingAMissingResourceReportsThatItDoesNotExist(string path, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.DeleteAsync(path, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDockerErrorAsync(response, message, ct);
    }

    [Fact]
    public async Task AttachWithStdinFailsFastBeforeLookingUpContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.PostAsync("/v1.43/containers/not-created/attach?stdin=1", null, ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, "Attach stdin", ct);
    }

    [Fact]
    public async Task ArchiveUploadRequiresDestinationPath()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/v1.43/containers/id/archive"),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDockerErrorAsync(response, "Archive destination path is required", ct);
    }

    [Theory]
    [InlineData("/v1.43/images/mysql/history")]
    [InlineData("/images/mysql/history")]
    // A namespaced repository keeps its slashes in the request path.
    [InlineData("/v1.43/images/mcr.microsoft.com/mssql/server/history")]
    public async Task ImageHistoryReportsThatWslcHasNoLayerHistoryInsteadOfAnUnknownEndpoint(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, "Image history is not supported", ct);
    }

    [Theory]
    [InlineData("/v1.43/containers/id/resize?h=40&w=120")]
    [InlineData("/containers/id/resize?h=40&w=120")]
    [InlineData("/v1.43/exec/id/resize?h=40&w=120")]
    public async Task TtyResizeReportsThatNoTtyExistsInsteadOfAnUnknownEndpoint(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.PostAsync(path, null, ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, "TTY resize is not supported", ct);
    }

    [Fact]
    public async Task EventStreamReportsThatWslcHasNoEventSourceInsteadOfAnUnknownEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync("/v1.54/events", ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, "Event streaming is not supported", ct);
    }

    [Fact]
    public async Task UnsupportedEndpointReturnsDockerNotFoundPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/v1.43/unimplemented"),
            ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDockerErrorAsync(response, "endpoint not implemented: PUT /v1.43/unimplemented", ct);
    }

    private static async Task AssertDockerErrorAsync(HttpResponseMessage response, string expectedMessage, CancellationToken ct)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Contains(expectedMessage, document.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }
}
