namespace WslcDockerSocket.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class DockerApiContractTest(DockerSocketApplicationFactory factory)
    : IClassFixture<DockerSocketApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

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
    [InlineData("AutoRemove", "AutoRemove")]
    [InlineData("NetworkMode", "default bridge network")]
    [InlineData("Tty", "TTY containers")]
    public async Task CreateWithUnsupportedOptionFailsFastInsteadOfClaimingSupport(string option, string message)
    {
        var ct = TestContext.Current.CancellationToken;
        object body = option switch
        {
            "Binds" => new { Image = "alpine:3.20.3", HostConfig = new { Binds = new[] { "C:/host:/container" } } },
            "AutoRemove" => new { Image = "alpine:3.20.3", HostConfig = new { AutoRemove = true } },
            "NetworkMode" => new { Image = "alpine:3.20.3", HostConfig = new { NetworkMode = "host" } },
            "Tty" => new { Image = "alpine:3.20.3", Tty = true },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null),
        };
        using var response = await _client.PostAsync("/v1.43/containers/create", JsonContent.Create(body), ct);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        await AssertDockerErrorAsync(response, message, ct);
    }

    [Fact]
    public async Task NetworkDeletionReportsThatTheNetworkDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.DeleteAsync("/v1.43/networks/not-created", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDockerErrorAsync(response, "No such network: not-created", ct);
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
    public async Task UnsupportedEndpointReturnsDockerNotFoundPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/v1.43/containers/id/archive"),
            ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDockerErrorAsync(response, "endpoint not implemented: PUT /v1.43/containers/id/archive", ct);
    }

    private static async Task AssertDockerErrorAsync(HttpResponseMessage response, string expectedMessage, CancellationToken ct)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Contains(expectedMessage, document.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }
}

public sealed class DockerSocketApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE", "true");
        builder.UseSetting("WSLC_DOCKER_SOCKET_ENABLE_TCP", "true");
        builder.UseSetting("WSLC_DOCKER_SOCKET_TCP_PORT", "23751");
    }
}
