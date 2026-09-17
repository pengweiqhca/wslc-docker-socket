using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using WslcDockerSocket.Api;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true);

builder.Host.UseWindowsService(options => options.ServiceName = "wslc-docker-socket");

var socketOptions = DockerSocketOptions.From(builder.Configuration);

var hyperVTcpAddress = socketOptions.DisableHyperVTcp
    ? null
    : IPAddress.Parse(socketOptions.HyperVTcpAddress!);

// Containers need a listener they can actually reach to get Docker-socket-mount requests translated into a
// DOCKER_HOST TCP endpoint (see DockerSocketMount). Loopback and the docker-bridge gateway are both internal
// to the WSLC VM/container and never reach Windows, so only the Hyper-V virtual switch address works here.
builder.Services.AddSingleton(hyperVTcpAddress is null
    ? DockerSocketMountAdvertisement.None
    : new DockerSocketMountAdvertisement(new Uri($"tcp://{hyperVTcpAddress}:{socketOptions.TcpPort}")));

builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>>(provider =>
    new ConfigureNamedOptions<KestrelServerOptions, DockerExecHijackConnectionHandler>(null,
        provider.GetRequiredService<DockerExecHijackConnectionHandler>(),
        (options, execHijackHandler) =>
        {
            void configure(ListenOptions listener) =>
                listener.Use(next => connection => execHijackHandler.HandleAsync(connection, next));

            if (socketOptions.EnableTcp) options.ListenLocalhost(socketOptions.TcpPort, configure);
            if (hyperVTcpAddress is not null) options.Listen(new IPEndPoint(hyperVTcpAddress, socketOptions.TcpPort), configure);
            if (!socketOptions.DisableNamedPipe) options.ListenNamedPipe(socketOptions.NamedPipe, configure);
        }));

builder.Services.PostConfigure<NamedPipeTransportOptions>(options =>
{
    if (socketOptions.DisableNamedPipe || !OperatingSystem.IsWindows()) return;

    options.CurrentUserOnly = false;

    var security = new PipeSecurity();

    security.AddAccessRule(new PipeAccessRule(
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
        AccessControlType.Allow));

    options.PipeSecurity = security;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton(provider => new WslcDockerEngine(new WslcCommandRunner(socketOptions.Session),
    new WslRuntimeDiagnosticsProvider(), provider.GetRequiredService<DockerSocketMountAdvertisement>()));
builder.Services.AddSingleton<DockerExecHijackConnectionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "WSLC Docker Socket API",
        Version = "v1",
        Description = "Docker Remote API compatibility surface backed by Microsoft WSL Containers.",
    }));

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<WslcDockerEngine>().Dispose);

// The version prefix must move into PathBase before routing, so UseRouting is placed explicitly:
// WebApplication would otherwise match endpoints ahead of all application middleware.
app.UseDockerApiVersionPathBase();
app.UseDockerApiExceptionHandler();
app.UseRouting();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "WSLC Docker Socket API";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "WSLC Docker Socket API v1");
});
app.MapDockerApi();
app.Run();

public partial class Program;
