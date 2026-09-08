namespace WslcDockerSocket.Tests;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;

[Trait("Category", "E2E")]
public sealed class RocketMqTestcontainersE2eTest
{
    private const string EnabledEnvironmentVariable = "WSLC_DOCKER_SOCKET_RUN_E2E";
    private const string DockerHostEnvironmentVariable = "DOCKER_HOST";
    private const string ExpectedDockerHost = "npipe://./pipe/wslc-docker-socket";

    [Fact]
    public async Task RocketMqProxyStartsThroughTheWslcDockerSocket()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable), "true",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Environment.GetEnvironmentVariable(DockerHostEnvironmentVariable), ExpectedDockerHost,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var port = GetAvailableTcpPort();
        await using var container = new ContainerBuilder("apache/rocketmq:5.3.3")
            .WithDockerEndpoint(ExpectedDockerHost)
            .WithEnvironment("TZ", "Asia/Shanghai")
            .WithEnvironment("JAVA_OPT_EXT", "-server -Xms512m -Xmx512m -Xmn256m")
            .WithExposedPort(port)
            .WithPortBinding(port, port)
            .WithEntrypoint("sh", "-c")
            .WithCommand(GetStartupScript(port))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("rocketmq-proxy startup successfully",
                    static options => options.WithTimeout(TimeSpan.FromMinutes(5)))
                .UntilCommandIsCompleted(["sh", "-c", "./mqadmin clusterList -n 127.0.0.1:9876 | grep -q DefaultCluster"],
                    static options => options.WithTimeout(TimeSpan.FromMinutes(3))
                        .WithInterval(TimeSpan.FromSeconds(2))))
            .Build();

        await container.StartAsync(ct);

        Assert.Equal(port, container.GetMappedPublicPort(port));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        Assert.True(client.Connected);
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetStartupScript(int port) =>
        "printf 'brokerIP1=127.0.0.1\\n' >> ../conf/broker.conf; " +
        $"printf '{{\"rocketMQClusterName\":\"DefaultCluster\",\"grpcServerPort\":{port.ToString(CultureInfo.InvariantCulture)}}}' > ../conf/rmq-proxy.json; " +
        "./mqnamesrv > /tmp/namesrv.log 2>&1 & " +
        "sleep 8; " +
        "exec ./mqbroker -n 127.0.0.1:9876 --enable-proxy -c ../conf/broker.conf";
}
