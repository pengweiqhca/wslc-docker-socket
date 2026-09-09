using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using WslcDockerSocket.Api;
using WslcDockerSocket.Api.Contracts;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Streaming;

namespace WslcDockerSocket.Tests;

public sealed class DockerCompatibilityBoundaryTest
{
    [Fact]
    public void IPv6HostBindingIsRejectedInsteadOfReportedAsIpv4()
    {
        var bindings = new Dictionary<string, List<DockerHostPortBinding>?>
        {
            ["8080/tcp"] = [new DockerHostPortBinding { HostIp = "::", HostPort = "18080" }],
        };

        var exception = Assert.Throws<DockerApiException>(() => DockerPortBinding.Parse(bindings));

        Assert.Equal(StatusCodes.Status501NotImplemented, exception.StatusCode);
        Assert.Contains("IPv6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleHostBindingsAreRejectedInsteadOfSilentlyDroppingOne()
    {
        var bindings = new Dictionary<string, List<DockerHostPortBinding>?>
        {
            ["8080/tcp"] =
            [
                new DockerHostPortBinding { HostPort = "18080" },
                new DockerHostPortBinding { HostPort = "28080" },
            ],
        };

        var exception = Assert.Throws<DockerApiException>(() => DockerPortBinding.Parse(bindings));

        Assert.Equal(StatusCodes.Status501NotImplemented, exception.StatusCode);
        Assert.Contains("Multiple host bindings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachSnapshotCanExceedLiveSubscriberQueueWithoutTruncation()
    {
        var output = new DockerOutputBuffer();
        for (var index = 0; index < 200; index++)
        {
            output.Append(DockerStreamType.Stdout, [(byte)index]);
        }

        using var subscription = output.Subscribe(includeSnapshot: true, includeStdout: true, includeStderr: true);
        output.Append(DockerStreamType.Stdout, [200]);
        output.Complete();

        var frames = new List<DockerOutputFrame>();
        await foreach (var frame in subscription.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            frames.Add(frame);
        }

        Assert.Equal(201, frames.Count);
    }

    [Fact]
    public void LogsFollowQueryIsRecognizedForStreamingLogs()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["follow"] = "true",
        });

        Assert.True(DockerQuery.ReadBoolean(query, "follow", false));
    }
}
