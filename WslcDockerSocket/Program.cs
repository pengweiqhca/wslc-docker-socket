using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using WslcDockerSocket.Api;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "wslc-docker-socket");

var socketOptions = DockerSocketOptions.From(builder.Configuration);

builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>>(provider =>
    new ConfigureNamedOptions<KestrelServerOptions, DockerExecHijackConnectionHandler>(null,
        provider.GetRequiredService<DockerExecHijackConnectionHandler>(),
        (options, execHijackHandler) =>
        {
            Action<ListenOptions> configure = listener =>
                listener.Use(next => connection => execHijackHandler.HandleAsync(connection, next));

            if (socketOptions.EnableTcp) options.ListenLocalhost(socketOptions.TcpPort, configure);
            if (!socketOptions.DisableNamedPipe) options.ListenNamedPipe(socketOptions.NamedPipe, configure);
        }));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton<WslcDockerEngine>().AddSingleton<DockerExecHijackConnectionHandler>();

var app = builder.Build();
var engine = app.Services.GetRequiredService<WslcDockerEngine>();
app.Lifetime.ApplicationStopping.Register(engine.Dispose);

app.UseDockerApiExceptionHandler();
app.MapDockerApi();
app.Run();

public partial class Program;
