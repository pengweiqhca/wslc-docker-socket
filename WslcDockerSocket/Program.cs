using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
#if DEBUG
using Microsoft.OpenApi;
#endif
using WslcDockerSocket.Api;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true);

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
            void Configure(ListenOptions listener) =>
                listener.Use(next => connection => execHijackHandler.HandleAsync(connection, next));

            if (socketOptions.EnableTcp) options.ListenLocalhost(socketOptions.TcpPort, Configure);
            if (hyperVTcpAddress is not null) options.Listen(new IPEndPoint(hyperVTcpAddress, socketOptions.TcpPort), Configure);
            if (!socketOptions.DisableNamedPipe) options.ListenNamedPipe(socketOptions.NamedPipe, Configure);
        }));

/*builder.Host.UseWindowsService(options => options.ServiceName = "wslc-docker-socket");

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
});*/

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton(provider => new WslcDockerEngine(new WslcCommandRunner(socketOptions.Session),
    new WslRuntimeDiagnosticsProvider(), provider.GetRequiredService<DockerSocketMountAdvertisement>()));
builder.Services.AddSingleton<DockerExecHijackConnectionHandler>();

#if DEBUG
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "WSLC Docker Socket API",
        Version = "v1",
        Description = "Docker Remote API compatibility surface backed by Microsoft WSL Containers.",
    }));
#endif

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<WslcDockerEngine>().Dispose);

// Keeps the console-mode process running as before; this only adds a tray icon that appears once the console
// window is minimized, and lets it be restored or the app exited from there.
var trayIcon = ConsoleTrayIcon.Start(app.Lifetime);
if (trayIcon is not null) app.Lifetime.ApplicationStopping.Register(trayIcon.Dispose);

// The version prefix must move into PathBase before routing, so UseRouting is placed explicitly:
// WebApplication would otherwise match endpoints ahead of all application middleware.
app.UseDockerApiVersionPathBase();
app.UseDockerApiExceptionHandler();
app.UseRouting();
#if DEBUG
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "WSLC Docker Socket API";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "WSLC Docker Socket API v1");
});
#endif
app.MapDockerApi();
app.Run();

public partial class Program;
