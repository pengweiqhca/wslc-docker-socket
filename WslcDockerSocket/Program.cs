using WslcDockerSocket.Api;
using WslcDockerSocket.Engine;
using WslcDockerSocket.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "wslc-docker-socket");

var socketOptions = DockerSocketOptions.From(builder.Configuration);
if (socketOptions.EnableTcp)
{
    builder.WebHost.UseUrls(socketOptions.TcpUrl);
}

builder.WebHost.ConfigureKestrel(options =>
{
    if (!socketOptions.DisableNamedPipe)
    {
        options.ListenNamedPipe(socketOptions.NamedPipe);
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton<WslcDockerEngine>();

var app = builder.Build();
var engine = app.Services.GetRequiredService<WslcDockerEngine>();
app.Lifetime.ApplicationStopping.Register(engine.Dispose);

app.UseDockerApiExceptionHandler();
app.MapDockerApi();
app.Run();

public partial class Program;
