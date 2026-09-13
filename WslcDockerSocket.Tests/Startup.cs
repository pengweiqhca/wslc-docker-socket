using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.DependencyInjection.AspNetCoreTesting;
using Xunit.DependencyInjection.Logging;

namespace WslcDockerSocket.Tests;

public sealed class Startup
{
    public const string PipeName = "wslc-test";

    static Startup() => TestcontainersSettings.ResourceReaperPrivilegedModeEnabled = false;

    public IHostBuilder CreateHostBuilder() => MinimalApiHostBuilderFactory.GetHostBuilder<Program>(false);

    public void ConfigureHost(IHostBuilder hostBuilder) => hostBuilder
        .UseEnvironment("Testing")
        .ConfigureHostConfiguration(builder =>
            builder.AddInMemoryCollection([
                new("WSLC_DOCKER_SOCKET_PIPE_NAME", PipeName),
            ]));

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(x => x.AddXunitOutput());

        services.AddHttpClient("docker", x => x.BaseAddress = new("http://pipe:/" + PipeName))
            .UseTestHandler();
    }
}
