using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Microsoft.WSL.Containers;
using WslcDockerSocket.Api;
using WslcDockerSocket.Api.Contracts;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Hosting;
using WslcDockerSocket.Streaming;

namespace WslcDockerSocket.Tests;

public sealed class DockerProtocolUnitTest
{
    [Theory]
    [InlineData("alpine", "docker.io/library/alpine:latest")]
    [InlineData("alpine:3.20.3", "docker.io/library/alpine:3.20.3")]
    [InlineData("ghcr.io/Org/App:Release", "ghcr.io/org/app:release")]
    [InlineData("index.docker.io/library/alpine", "docker.io/library/alpine:latest")]
    public void ImageReferenceCanonicalizesDockerCompatibleNames(string input, string expected)
    {
        Assert.Equal(expected, DockerImageReference.Parse(input).CanonicalName);
    }

    [Fact]
    public async Task RawStreamFrameUsesDockerEightByteMultiplexHeader()
    {
        await using var stream = new MemoryStream();
        await DockerStreams.WriteFrameAsync(stream,
            new DockerOutputFrame(DockerStreamType.Stderr, Encoding.UTF8.GetBytes("err")),
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 3, (byte)'e', (byte)'r', (byte)'r' }, stream.ToArray());
    }

    [Fact]
    public void TcpAndUdpBindingsForTheSameContainerPortRemainDistinct()
    {
        var bindings = DockerPortBinding.Parse(new Dictionary<string, List<DockerHostPortBinding>?>
        {
            ["8080/tcp"] = [new DockerHostPortBinding { HostPort = "18080" }],
            ["8080/udp"] = [new DockerHostPortBinding { HostPort = "28080" }],
        });

        var response = DockerPortBinding.ToDockerResponse(bindings, new Dictionary<DockerPortKey, ushort>
        {
            [bindings[0].Key] = 18080,
            [bindings[1].Key] = 28080,
        });

        Assert.Contains("18080", JsonSerializer.Serialize(response["8080/tcp"]), StringComparison.Ordinal);
        Assert.Contains("28080", JsonSerializer.Serialize(response["8080/udp"]), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeInspectionKeepsProtocolInResolvedPortKeys()
    {
        const string inspect = "{\"ports\":[{\"ContainerPort\":8080,\"HostPort\":18080,\"Protocol\":\"tcp\"},{\"ContainerPort\":8080,\"HostPort\":28080,\"Protocol\":\"udp\"}]}";

        var result = DockerPortBinding.ParseRuntimeInspect(inspect);

        Assert.Equal((ushort)18080, result[new DockerPortKey(8080, PortProtocol.TCP)]);
        Assert.Equal((ushort)28080, result[new DockerPortKey(8080, PortProtocol.UDP)]);
    }

    [Fact]
    public async Task SlowAttachSubscriberIsCompletedInsteadOfGrowingWithoutBound()
    {
        var output = new DockerOutputBuffer();
        using var subscription = output.Subscribe(includeSnapshot: false, includeStdout: true, includeStderr: true);
        for (var index = 0; index <= 128; index++)
        {
            output.Append(DockerStreamType.Stdout, [(byte)index]);
        }

        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in subscription.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.IsType<IOException>(exception);
    }

    [Fact]
    public void StreamOptionsRespectLogsStreamAndOutputFlags()
    {
        var options = DockerStreamOptions.FromQuery(new QueryCollection(new Dictionary<string, StringValues>
        {
            ["logs"] = "1",
            ["stream"] = "false",
            ["stdout"] = "true",
            ["stderr"] = "0",
        }));

        Assert.True(options.Logs);
        Assert.False(options.Stream);
        Assert.True(options.IncludeStdout);
        Assert.False(options.IncludeStderr);
    }

    [Fact]
    public void TcpListenerIsOptInAndAtLeastOneListenerIsRequired()
    {
        var defaultOptions = DockerSocketOptions.From(new ConfigurationBuilder().AddInMemoryCollection().Build());
        Assert.False(defaultOptions.EnableTcp);
        Assert.False(defaultOptions.DisableNamedPipe);

        var noListener = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WSLC_DOCKER_SOCKET_DISABLE_NAMED_PIPE"] = "true",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => DockerSocketOptions.From(noListener));
    }
}
