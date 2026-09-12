using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
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

app.UseDockerApiExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "WSLC Docker Socket API";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "WSLC Docker Socket API v1");
});
app.MapDockerApi();
app.Run();

public partial class Program;
