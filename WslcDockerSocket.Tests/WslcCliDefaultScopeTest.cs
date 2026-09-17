using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using WslcDockerSocket.Api;
using WslcDockerSocket.Api.Contracts;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Streaming;

namespace WslcDockerSocket.Tests;

public sealed class WslcCliDefaultScopeTest
{
    [Fact]
    public async Task ContainerCatalogListsOnlyTheUnqualifiedDefaultScope()
    {
        var runner = new RecordingRunner(new WslcCommandResult("{\"Id\":\"container-1\",\"Name\":\"default-container\",\"Image\":\"redis:latest\",\"State\":2,\"CreatedAt\":1}", string.Empty, 0));
        var catalog = new WslcCliContainerCatalog(runner);
        var containers = await catalog.ListAsync(TestContext.Current.CancellationToken);
        var container = Assert.Single(containers);
        Assert.Equal("container-1", container.Id);
        Assert.Equal("default-container", container.Name);
        Assert.Equal(["container", "list", "-a", "--format", "json"], Assert.Single(runner.Commands));
        AssertDefaultScope(runner);
    }

    [Fact]
    public void ConfiguredSessionIsSelectedBeforeTheSubcommand()
    {
        // wslc rejects --session after the subcommand, and an elevated token otherwise gets its own session.
        Assert.Equal(["--session", "wslc-cli-demo", "container", "list", "-a", "--format", "json"],
            WslcCommandRunner.BuildArguments(["container", "list", "-a", "--format", "json"], "wslc-cli-demo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetSessionLeavesTheCommandUnqualified(string? session) =>
        Assert.Equal(["container", "list"], WslcCommandRunner.BuildArguments(["container", "list"], session));

    [Fact]
    public async Task ContainerCatalogReadsTheDockerAlignedListShape()
    {
        // WSLC 2.9.10 aligned this output with Docker's CLI: ID, Names, and text State/CreatedAt.
        var runner = new RecordingRunner(new WslcCommandResult(
            "{\"Command\":\"\\\"docker-entrypoint.s…\\\"\",\"CreatedAt\":\"2026-09-12 20:22:55 +0800 GMT+8\",\"ID\":\"19c58a00a160\","
            + "\"Image\":\"mysql\",\"Names\":\"mysql\",\"State\":\"exited\",\"Status\":\"Exited (0) 3 minutes ago\"}",
            string.Empty,
            0));

        var container = Assert.Single(await new WslcCliContainerCatalog(runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("19c58a00a160", container.Id);
        Assert.Equal("mysql", container.Name);
        Assert.Equal("mysql", container.Image);
        Assert.Equal("exited", container.DockerState);
        Assert.Equal("Exited (0) 3 minutes ago", container.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T20:22:55+08:00", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            container.CreatedAt);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ContainerCatalogStillReadsTheNativeNumericListShape()
    {
        var runner = new RecordingRunner(new WslcCommandResult(
            "{\"CreatedAt\":1789215775,\"Id\":\"19c58a00a160\",\"Image\":\"mysql\",\"Name\":\"mysql\",\"State\":2}",
            string.Empty,
            0));

        var container = Assert.Single(await new WslcCliContainerCatalog(runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("19c58a00a160", container.Id);
        Assert.Equal("mysql", container.Name);
        Assert.Equal("running", container.DockerState);
        Assert.Equal(1789215775, container.CreatedAt);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ContainerListAppliesLabelFiltersInsteadOfReturningEveryContainer()
    {
        // Testcontainers' reaper selects by its own session label and then deletes what it is given, so an
        // unfiltered superset would destroy unrelated containers.
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":\"2026-09-12 20:22:55 +0800 GMT+8\",\"ID\":\"aaaa11112222\",\"Image\":\"redis\",\"Names\":\"owned\",\"State\":\"running\"}\n"
                + "{\"CreatedAt\":\"2026-09-12 20:22:55 +0800 GMT+8\",\"ID\":\"bbbb33334444\",\"Image\":\"mysql\",\"Names\":\"unrelated\",\"State\":\"running\"}",
                string.Empty,
                0)
            : new WslcCommandResult(
                "[{\"Id\":\"aaaa11112222\",\"Labels\":{\"org.testcontainers.session-id\":\"session-1\"}},"
                + "{\"Id\":\"bbbb33334444\",\"Labels\":{\"com.example.other\":\"yes\"}}]",
                string.Empty,
                0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var matching = await engine.ListContainersAsync(
            new QueryCollection(new Dictionary<string, StringValues>
            {
                ["all"] = "1",
                ["filters"] = """{"label":["org.testcontainers.session-id=session-1"]}""",
            }),
            TestContext.Current.CancellationToken);

        var listed = JsonSerializer.SerializeToElement(Assert.Single(matching));
        Assert.Equal("/owned", listed.GetProperty("Names")[0].GetString());
    }

    [Fact]
    public async Task ContainerListRejectsFiltersItCannotApply()
    {
        var runner = new RecordingRunner(new WslcCommandResult(string.Empty, string.Empty, 0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var exception = await Assert.ThrowsAsync<DockerApiException>(() => engine.ListContainersAsync(
            new QueryCollection(new Dictionary<string, StringValues> { ["filters"] = """{"before":["x"]}""" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(StatusCodes.Status501NotImplemented, exception.StatusCode);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ContainerListReportsLabelsAndCommandFromInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":\"2026-09-12 20:22:55 +0800 GMT+8\",\"ID\":\"19c58a00a160\",\"Image\":\"mysql\",\"Names\":\"mysql\",\"State\":\"running\",\"Status\":\"Up 3 minutes\"}",
                string.Empty,
                0)
            : new WslcCommandResult(
                "[{\"Id\":\"19c58a00a160\",\"Image\":\"sha256:21ed0f5cc494\",\"Labels\":{\"com.docker.compose.project\":\"demo\"},"
                + "\"Config\":{\"Image\":\"mysql:latest\",\"Entrypoint\":[\"docker-entrypoint.sh\"],\"Cmd\":[\"mysqld\"]}}]",
                string.Empty,
                0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var listed = Assert.Single(await engine.ListContainersAsync(new QueryCollection(), TestContext.Current.CancellationToken));
        var container = JsonSerializer.SerializeToElement(listed);

        Assert.Equal("demo", container.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString());
        // Docker reports the created-from reference and the image digest separately.
        Assert.Equal("mysql:latest", container.GetProperty("Image").GetString());
        Assert.Equal("sha256:21ed0f5cc494", container.GetProperty("ImageID").GetString());
        Assert.Equal("docker-entrypoint.sh mysqld", container.GetProperty("Command").GetString());
        Assert.Equal("Up 3 minutes", container.GetProperty("Status").GetString());
        Assert.Equal("running", container.GetProperty("State").GetString());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ContainerListReportsPublishedPortsAndNetworkAddressFromOneBatchedInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":1789215775,\"Id\":\"19c58a00a160\",\"Image\":\"mysql\",\"Name\":\"mysql\",\"State\":2}",
                string.Empty,
                0)
            : command.SequenceEqual(["container", "inspect", "19c58a00a160", "--format", "json"])
                ? new WslcCommandResult(
                    "[{\"Id\":\"19c58a00a160\",\"Ports\":{\"3306/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"3306\"}],\"33060/tcp\":[]},"
                    + "\"NetworkSettings\":{\"Networks\":{\"bridge\":{\"Gateway\":\"172.17.0.1\",\"IPAddress\":\"172.17.0.3\"}}}}]",
                    string.Empty,
                    0)
                : throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}"));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var listed = Assert.Single(await engine.ListContainersAsync(new QueryCollection(), TestContext.Current.CancellationToken));
        var container = JsonSerializer.SerializeToElement(listed);

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal("172.17.0.3", container.GetProperty("NetworkSettings").GetProperty("Networks")
            .GetProperty("bridge").GetProperty("IPAddress").GetString());
        var ports = container.GetProperty("Ports");
        Assert.Equal(2, ports.GetArrayLength());
        Assert.Equal("127.0.0.1", ports[0].GetProperty("IP").GetString());
        Assert.Equal(3306, ports[0].GetProperty("PrivatePort").GetInt32());
        Assert.Equal(3306, ports[0].GetProperty("PublicPort").GetInt32());
        Assert.Equal("tcp", ports[0].GetProperty("Type").GetString());
        // An exposed but unpublished port carries no host address or public port.
        Assert.Equal(33060, ports[1].GetProperty("PrivatePort").GetInt32());
        Assert.False(ports[1].TryGetProperty("PublicPort", out _));
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ContainerListKeepsEmptyPortsWhenInspectOmitsTheContainer()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":1789215775,\"Id\":\"19c58a00a160\",\"Image\":\"mysql\",\"Name\":\"mysql\",\"State\":2}",
                string.Empty,
                0)
            // A container removed since the listing must not fail the whole list response.
            : new WslcCommandResult(string.Empty, "Container '19c58a00a160' not found.", 1));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var listed = Assert.Single(await engine.ListContainersAsync(new QueryCollection(), TestContext.Current.CancellationToken));
        var container = JsonSerializer.SerializeToElement(listed);

        Assert.Empty(container.GetProperty("Ports").EnumerateArray());
        Assert.Empty(container.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ContainerInspectNormalizesDockerNameAndPorts()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult("{\"Id\":\"container-1\",\"Name\":\"default-container\",\"Image\":\"redis\",\"State\":2}", "", 0)
            : new WslcCommandResult("[{\"Name\":\"default-container\",\"Ports\":{\"80/tcp\":[{\"HostPort\":\"8080\"}]}}]", "", 0));
        var inspect = await new WslcCliContainerCatalog(runner).InspectAsync("container-1", TestContext.Current.CancellationToken);
        Assert.Equal("/default-container", inspect.GetProperty("Name").GetString());
        Assert.True(inspect.GetProperty("NetworkSettings").GetProperty("Ports").TryGetProperty("80/tcp", out _));
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ResourceCatalogListsAndInspectsOnlyTheUnqualifiedDefaultScope()
    {
        var runner = new RecordingRunner(command =>
        {
            if (command.SequenceEqual(["volume", "list", "--format", "json"])) return new WslcCommandResult("{\"Name\":\"default-volume\",\"Id\":\"volume-1\"}", string.Empty, 0);
            if (command.SequenceEqual(["volume", "inspect", "default-volume", "--format", "json"])) return new WslcCommandResult("[{\"Name\":\"default-volume\",\"Driver\":\"local\"}]", string.Empty, 0);
            throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}");
        });
        var resource = Assert.Single(await new WslcCliResourceCatalog(runner).ListAndInspectAsync("volume", TestContext.Current.CancellationToken));
        Assert.Equal("default-volume", resource.GetProperty("Name").GetString());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task LifecycleUsesVerifiedUnqualifiedCliVectorsAndReconcilesCreatedContainer()
    {
        var state = 1;
        var runner = new RecordingRunner(command =>
        {
            if (command.Take(2).SequenceEqual(["container", "create"])) return new WslcCommandResult("created-1\n", "", 0);
            if (command.SequenceEqual(["container", "list", "-a", "--format", "json"])) return new WslcCommandResult($"{{\"Id\":\"created-1\",\"Name\":\"demo\",\"Image\":\"docker.io/library/alpine:latest\",\"State\":{state}}}", "", 0);
            if (command.SequenceEqual(["container", "start", "created-1"])) { state = 2; return new WslcCommandResult("", "", 0); }
            if (command.SequenceEqual(["container", "stop", "created-1"])) { state = 3; return new WslcCommandResult("", "", 0); }
            if (command.SequenceEqual(["container", "remove", "--force", "created-1"])) return new WslcCommandResult("", "", 0);
            throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}");
        });
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());
        var id = await engine.CreateContainerAsync("demo", new DockerCreateContainerRequest
        {
            Image = "alpine",
            Hostname = "host",
            Env = ["A=B"],
            Entrypoint = ["/init"],
            Labels = new Dictionary<string, string> { ["purpose"] = "test" },
            HostConfig = new DockerHostConfig { PortBindings = new Dictionary<string, List<DockerHostPortBinding>?> { ["8080/tcp"] = [new DockerHostPortBinding { HostPort = "18080" }] } },
            Cmd = ["echo", "ok"],
        }, TestContext.Current.CancellationToken);
        Assert.Equal("created-1", id);
        Assert.True(await engine.StartContainerAsync(id, TestContext.Current.CancellationToken));
        Assert.True(await engine.StopContainerAsync(id, TestContext.Current.CancellationToken));
        await engine.DeleteContainerAsync(id, true, TestContext.Current.CancellationToken);
        Assert.Equal(["container", "create", "--name", "demo", "--hostname", "host", "--env", "A=B", "--entrypoint", "/init", "--label", "purpose=test", "--publish", "18080:8080", "docker.io/library/alpine:latest", "echo", "ok"], runner.Commands[0]);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task CreateWithAutoRemoveUsesVerifiedNativeRmOption()
    {
        var runner = new RecordingRunner(command => command.Take(2).SequenceEqual(["container", "create"])
            ? new WslcCommandResult("created-1\n", string.Empty, 0)
            : command.SequenceEqual(["container", "list", "-a", "--format", "json"])
                ? new WslcCommandResult("{\"Id\":\"created-1\",\"Name\":\"demo\",\"Image\":\"docker.io/library/alpine:latest\",\"State\":1}", string.Empty, 0)
                : throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}"));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        await engine.CreateContainerAsync("demo", new DockerCreateContainerRequest
        {
            Image = "alpine",
            HostConfig = new DockerHostConfig { AutoRemove = true },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["container", "create", "--rm", "--name", "demo", "docker.io/library/alpine:latest"], runner.Commands[0]);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task StopSucceedsWhenWslcAutoRemoveDeletesTheContainer()
    {
        var removed = false;
        var runner = new RecordingRunner(command =>
        {
            if (command.SequenceEqual(["container", "list", "-a", "--format", "json"]))
            {
                return removed
                    ? new WslcCommandResult(string.Empty, string.Empty, 0)
                    : new WslcCommandResult("{\"Id\":\"container-1\",\"Name\":\"demo\",\"Image\":\"redis\",\"State\":2}", string.Empty, 0);
            }

            if (command.SequenceEqual(["container", "stop", "container-1"]))
            {
                removed = true;
                return new WslcCommandResult(string.Empty, string.Empty, 0);
            }

            throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}");
        });
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        Assert.True(await engine.StopContainerAsync("container-1", TestContext.Current.CancellationToken));
        Assert.Equal(2, runner.Commands.Count);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task AnonymousDockerRegistryAuthIsAllowedBeforeImageCliCommand()
    {
        var runner = new RecordingRunner(new WslcCommandResult(string.Empty, string.Empty, 0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());
        var anonymousAuth = EncodeRegistryAuth("""{"username":"","password":"","serveraddress":"https://index.docker.io/v1/"}""");

        await engine.PullImageAsync(DockerImageReference.Parse("redis"), anonymousAuth, TestContext.Current.CancellationToken);

        Assert.Equal(["image", "pull", "docker.io/library/redis:latest"], Assert.Single(runner.Commands));
    }

    [Fact]
    public async Task CredentialBearingDockerRegistryAuthIsRejectedBeforeImageCliCommand()
    {
        var runner = new RecordingRunner(new WslcCommandResult(string.Empty, string.Empty, 0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());
        var credentialedAuth = EncodeRegistryAuth("""{"username":"user","password":"secret"}""");

        var exception = await Assert.ThrowsAsync<DockerApiException>(() => engine.PullImageAsync(DockerImageReference.Parse("redis"), credentialedAuth, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCodes.Status501NotImplemented, exception.StatusCode);
        Assert.Empty(runner.Commands);
        Assert.DoesNotContain(credentialedAuth, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedRegistryAuthIsRejectedBeforeImageCliCommand()
    {
        var runner = new RecordingRunner(new WslcCommandResult(string.Empty, string.Empty, 0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        var exception = await Assert.ThrowsAsync<DockerApiException>(() => engine.PullImageAsync(DockerImageReference.Parse("redis"), "not-base64", TestContext.Current.CancellationToken));

        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task NonemptyRegistryAuthIsRejectedBeforeImageCliCommand()
    {
        var runner = new RecordingRunner(new WslcCommandResult("", "", 0));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());
        var exception = await Assert.ThrowsAsync<DockerApiException>(() => engine.PullImageAsync(DockerImageReference.Parse("alpine"), "sensitive-token", TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Empty(runner.Commands);
        Assert.DoesNotContain("sensitive-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecPreservesSeparateHostPipesAndExitCode()
    {
        var runner = new RecordingRunner(command =>
        {
            Assert.Equal(["container", "exec", "container-1", "sh", "-c", "echo out; echo err >&2; exit 7"], command);
            return new WslcCommandResult("out\n", "err\n", 7);
        });
        var exec = new DockerExecState("exec-1", "container-1", ["sh", "-c", "echo out; echo err >&2; exit 7"]);
        var result = await exec.StartAsync(runner, TestContext.Current.CancellationToken);
        Assert.Equal("out\n", System.Text.Encoding.UTF8.GetString(result.StandardOutputBytes));
        Assert.Equal("err\n", System.Text.Encoding.UTF8.GetString(result.StandardErrorBytes));
        Assert.Equal(7, exec.ExitCode);
        Assert.False(exec.Running);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task FollowingLogsForwardsSeparatePipesWithAnUnqualifiedCliVector()
    {
        var runner = new RecordingRunner(
            command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
                ? new WslcCommandResult("{\"Id\":\"container-1\",\"Name\":\"default-container\",\"Image\":\"redis\",\"State\":2}", "", 0)
                : throw new Xunit.Sdk.XunitException($"Unexpected finite command: {string.Join(' ', command)}"),
            async (command, writeFrameAsync, ct) =>
            {
                Assert.Equal(["container", "logs", "--follow", "container-1"], command);
                await writeFrameAsync(new DockerOutputFrame(DockerStreamType.Stdout, "out\\n"u8.ToArray()), ct);
                await writeFrameAsync(new DockerOutputFrame(DockerStreamType.Stderr, "err\\n"u8.ToArray()), ct);
            });
        var frames = new List<DockerOutputFrame>();
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        await engine.StreamLogsAsync("container-1", (frame, _) =>
        {
            frames.Add(frame);
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Collection(frames,
            frame => { Assert.Equal(DockerStreamType.Stdout, frame.Stream); Assert.Equal("out\\n"u8.ToArray(), frame.Data); },
            frame => { Assert.Equal(DockerStreamType.Stderr, frame.Stream); Assert.Equal("err\\n"u8.ToArray(), frame.Data); });
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogUsesUnqualifiedListAndInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["image", "list", "--format", "json"])
            ? new WslcCommandResult("{\"ID\":\"sha256:1\",\"Name\":\"alpine:latest\"}", "", 0)
            : new WslcCommandResult("[{\"ID\":\"sha256:1\",\"RepoTags\":[\"alpine:latest\"]}]", "", 0));
        var catalog = new WslcCliImageCatalog(runner);
        var image = Assert.Single(await catalog.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("sha256:1", image.GetProperty("Id").GetString());
        Assert.Equal("alpine:latest", image.GetProperty("RepoTags")[0].GetString());
        Assert.Equal("sha256:1", (await catalog.InspectAsync("alpine:latest", TestContext.Current.CancellationToken)).GetProperty("Id").GetString());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogResolvesDockerTagToStableIdBeforeInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["image", "list", "--format", "json"])
            ? new WslcCommandResult("{\"ID\":\"923b31bc216c\",\"Repository\":\"redis\",\"Tag\":\"latest\"}", string.Empty, 0)
            : command.SequenceEqual(["image", "inspect", "923b31bc216c", "--format", "json"])
                ? new WslcCommandResult("[{\"ID\":\"sha256:923b31bc216c\",\"RepoTags\":[\"redis:latest\"]}]", string.Empty, 0)
                : throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}"));
        var catalog = new WslcCliImageCatalog(runner);

        var image = await catalog.InspectAsync("redis:latest", TestContext.Current.CancellationToken);

        Assert.Equal("sha256:923b31bc216c", image.GetProperty("Id").GetString());
        Assert.Equal(["image", "list", "--format", "json"], runner.Commands[0]);
        Assert.Equal(["image", "inspect", "923b31bc216c", "--format", "json"], runner.Commands[1]);
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogProjectsNativeSummaryToDockerNumericFields()
    {
        var runner = new RecordingRunner(new WslcCommandResult(
            "{\"Containers\":\"0\",\"Created\":1788136973,\"Id\":\"sha256:923b\",\"Repository\":\"redis\",\"Size\":145571112,\"Tag\":\"latest\"}",
            string.Empty,
            0));

        var image = Assert.Single(await new WslcCliImageCatalog(runner).ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("sha256:923b", image.GetProperty("Id").GetString());
        Assert.Equal(1788136973, image.GetProperty("Created").GetInt64());
        Assert.Equal(145571112, image.GetProperty("Size").GetInt64());
        Assert.Equal(-1, image.GetProperty("Containers").GetInt64());
        Assert.Equal("redis:latest", image.GetProperty("RepoTags")[0].GetString());
        Assert.False(image.TryGetProperty("Repository", out _));
        Assert.False(image.TryGetProperty("Tag", out _));
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ArchiveUploadStreamsRawTarToTheCanonicalContainerId()
    {
        var archive = "archive-payload"u8.ToArray();
        var runner = new RecordingRunner(command => command.SequenceEqual(["container", "list", "-a", "--format", "json"])
            ? new WslcCommandResult("{\"Id\":\"container-1\",\"Name\":\"demo\",\"Image\":\"redis\",\"State\":2}", string.Empty, 0)
            : command.SequenceEqual(["container", "cp", "-", "container-1:/target"])
                ? new WslcCommandResult(string.Empty, string.Empty, 0)
                : throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}"));
        using var engine = new WslcDockerEngine(runner, new WslRuntimeDiagnosticsProvider());

        await engine.CopyArchiveToContainerAsync("demo", "/target", new MemoryStream(archive), TestContext.Current.CancellationToken);

        Assert.Equal(["container", "list", "-a", "--format", "json"], runner.Commands[0]);
        Assert.Equal(["container", "cp", "-", "container-1:/target"], runner.Commands[1]);
        Assert.Equal(archive, Assert.Single(runner.StandardInputs));
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogTakesExactSizeAndCreatedFromOneBatchedInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["image", "list", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":\"2026-08-25 08:48:50 +0800 GMT+8\",\"ID\":\"923b31bc216c\",\"Repository\":\"redis\",\"Size\":\"146MB\",\"Tag\":\"latest\"}\n"
                + "{\"CreatedAt\":\"2024-12-12 19:10:47 +0800 GMT+8\",\"ID\":\"d8fa92ef30d3\",\"Repository\":\"confluentinc/cp-kafka\",\"Size\":\"1.08GB\",\"Tag\":\"7.8.0\"}",
                string.Empty,
                0)
            : command.SequenceEqual(["image", "inspect", "923b31bc216c", "d8fa92ef30d3", "--format", "json"])
                ? new WslcCommandResult(
                    "[{\"Created\":\"2026-08-25T00:48:50.305752157Z\",\"Id\":\"sha256:923b31bc216c39d25b1c984d86704f49dbd5184f2ad949ca88fd4c23264c74e7\",\"Size\":145571112},"
                    + "{\"Created\":\"2024-12-12T11:10:47.528661763Z\",\"Id\":\"sha256:d8fa92ef30d3d7d1f7a1699d145d6002c38860f48d71a8ae645373e9d2245e8d\",\"Size\":1076520321}]",
                    string.Empty,
                    0)
                : throw new Xunit.Sdk.XunitException($"Unexpected command: {string.Join(' ', command)}"));

        var images = await new WslcCliImageCatalog(runner).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(145_571_112, images[0].GetProperty("Size").GetInt64());
        Assert.Equal(145_571_112, images[0].GetProperty("VirtualSize").GetInt64());
        Assert.Equal(1_787_618_930, images[0].GetProperty("Created").GetInt64());
        Assert.Equal("redis:latest", images[0].GetProperty("RepoTags")[0].GetString());
        Assert.Equal(1_076_520_321, images[1].GetProperty("Size").GetInt64());
        Assert.Equal(1_734_001_847, images[1].GetProperty("Created").GetInt64());
        Assert.Equal("confluentinc/cp-kafka:7.8.0", images[1].GetProperty("RepoTags")[0].GetString());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogKeepsListValuesForAnImageMissingFromTheBatchedInspect()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["image", "list", "--format", "json"])
            ? new WslcCommandResult(
                "{\"CreatedAt\":\"2026-08-25 08:48:50 +0800 GMT+8\",\"ID\":\"923b31bc216c\",\"Repository\":\"redis\",\"Size\":\"146MB\",\"Tag\":\"latest\"}\n"
                + "{\"CreatedAt\":\"2026-06-10 11:13:44 +0800 GMT+8\",\"ID\":\"b9aeed16ad26\",\"Repository\":\"mcr.microsoft.com/mssql/server\",\"Size\":\"1.83GB\",\"Tag\":\"latest\"}",
                string.Empty,
                0)
            // WSLC reports the removed image on stderr with a nonzero exit but still returns the survivor.
            : new WslcCommandResult(
                "[{\"Created\":\"2026-08-25T00:48:50.305752157Z\",\"Id\":\"sha256:923b31bc216c39d25b1c984d86704f49dbd5184f2ad949ca88fd4c23264c74e7\",\"Size\":145571112}]",
                "Image 'b9aeed16ad26' not found.",
                1));

        var images = await new WslcCliImageCatalog(runner).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(145_571_112, images[0].GetProperty("Size").GetInt64());
        Assert.Equal(1_830_000_000, images[1].GetProperty("Size").GetInt64());
        Assert.Equal(DateTimeOffset.Parse("2026-06-10T11:13:44+08:00", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            images[1].GetProperty("Created").GetInt64());
        AssertDefaultScope(runner);
    }

    [Fact]
    public async Task ImageCatalogFallsBackToRenderedListValuesWhenInspectReturnsNothing()
    {
        var runner = new RecordingRunner(command => command.SequenceEqual(["image", "list", "--format", "json"])
            ? new WslcCommandResult(
                "{\"Containers\":\"0\",\"CreatedAt\":\"2026-08-25 08:48:50 +0800 GMT+8\",\"CreatedSince\":\"2 weeks ago\",\"ID\":\"923b31bc216c\",\"Repository\":\"redis\",\"SharedSize\":\"N/A\",\"Size\":\"146MB\",\"Tag\":\"latest\"}\n"
                + "{\"Containers\":\"0\",\"CreatedAt\":\"2026-06-10 11:13:44 +0800 GMT+8\",\"ID\":\"b9aeed16ad26\",\"Repository\":\"mcr.microsoft.com/mssql/server\",\"Size\":\"1.83GB\",\"Tag\":\"latest\"}",
                string.Empty,
                0)
            // Inspect failing outright must degrade to the rendered list values instead of failing the listing.
            : new WslcCommandResult(string.Empty, "inspect unavailable", 1));

        var images = await new WslcCliImageCatalog(runner).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.Parse("2026-08-25T08:48:50+08:00", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            images[0].GetProperty("Created").GetInt64());
        Assert.Equal(146_000_000, images[0].GetProperty("Size").GetInt64());
        Assert.Equal(146_000_000, images[0].GetProperty("VirtualSize").GetInt64());
        Assert.Equal("redis:latest", images[0].GetProperty("RepoTags")[0].GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-06-10T11:13:44+08:00", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            images[1].GetProperty("Created").GetInt64());
        Assert.Equal(1_830_000_000, images[1].GetProperty("Size").GetInt64());
        Assert.Equal("mcr.microsoft.com/mssql/server:latest", images[1].GetProperty("RepoTags")[0].GetString());
        AssertDefaultScope(runner);
    }

    private static string EncodeRegistryAuth(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
        .Replace('+', '-').Replace('/', '_');

    private static void AssertDefaultScope(RecordingRunner runner)
    {
        Assert.DoesNotContain(runner.Commands.SelectMany(command => command), argument => string.Equals(argument, "--session", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands.SelectMany(command => command), argument => string.Equals(argument, "system", StringComparison.Ordinal));
    }

    private sealed class RecordingRunner(
        Func<IReadOnlyList<string>, WslcCommandResult> resultFactory,
        Func<IReadOnlyList<string>, Func<DockerOutputFrame, CancellationToken, ValueTask>, CancellationToken, Task> streamAsync)
        : IWslcCommandRunner
    {
        public RecordingRunner(WslcCommandResult result) : this(_ => result)
        {
        }

        public RecordingRunner(Func<IReadOnlyList<string>, WslcCommandResult> resultFactory)
            : this(resultFactory, (_, _, _) => Task.CompletedTask)
        {
        }

        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<byte[]> StandardInputs { get; } = [];

        public Task<WslcCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            // Image listing inspects images concurrently, so recording must be thread safe.
            lock (Commands) Commands.Add([.. command]);
            return Task.FromResult(resultFactory(command));
        }

        public async Task<WslcCommandResult> RunWithStandardInputAsync(IReadOnlyList<string> command, Stream input,
            CancellationToken ct)
        {
            Commands.Add([.. command]);
            await using var captured = new MemoryStream();
            await input.CopyToAsync(captured, ct);
            StandardInputs.Add(captured.ToArray());
            return resultFactory(command);
        }

        public Task StreamAsync(IReadOnlyList<string> command,
            Func<DockerOutputFrame, CancellationToken, ValueTask> writeFrameAsync, CancellationToken ct)
        {
            Commands.Add([.. command]);
            return streamAsync(command, writeFrameAsync, ct);
        }
    }
}
