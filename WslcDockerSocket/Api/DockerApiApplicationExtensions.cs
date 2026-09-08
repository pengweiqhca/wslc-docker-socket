namespace WslcDockerSocket.Api;

using System.Text;
using System.Text.Json;
using Contracts;
using Engine;
using Streaming;

internal static class DockerApiApplicationExtensions
{
    public static IApplicationBuilder UseDockerApiExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (DockerApiException exception)
            {
                await DockerResults.WriteErrorAsync(context, exception.StatusCode, exception.Message,
                        context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DockerApi")
                    .LogError(exception, "Docker API request {Method} {Path} failed.", context.Request.Method,
                        context.Request.Path);
                await DockerResults.WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                        exception.Message, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        });
    }

    public static void MapDockerApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/", () => Results.Text("wslc-docker-socket", "text/plain"));
        MapGet(app, "/_ping", () => Results.Text("OK", "text/plain"));
        MapGet(app, "/version", () => Results.Json(WslcDockerEngine.GetVersion()));
        MapGet(app, "/info", async (WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.GetInfoAsync(ct).ConfigureAwait(false)));

        MapGet(app, "/images/json", (WslcDockerEngine engine) => Results.Json(engine.ListImages()));
        MapGet(app, "/images/{image}/json", (string image, WslcDockerEngine engine) =>
            Results.Json(engine.InspectImage(image)));
        MapPost(app, "/images/create", PullImageAsync);

        MapPost(app, "/containers/create", (HttpContext context, DockerCreateContainerRequest request,
            WslcDockerEngine engine) =>
        {
            var container = engine.CreateContainer(context.Request.Query["name"].ToString(), request);
            return Results.Json(new { container.Id, Warnings = Array.Empty<string>() },
                statusCode: StatusCodes.Status201Created);
        });
        MapPost(app, "/containers/{id}/start", (string id, WslcDockerEngine engine) =>
            Results.StatusCode(engine.StartContainer(id) ? StatusCodes.Status204NoContent : StatusCodes.Status304NotModified));
        MapPost(app, "/containers/{id}/stop", (string id, WslcDockerEngine engine) =>
            Results.StatusCode(engine.StopContainer(id) ? StatusCodes.Status204NoContent : StatusCodes.Status304NotModified));
        MapPost(app, "/containers/{id}/wait", WaitForContainerAsync);
        MapGet(app, "/containers/json", async (HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ListContainersAsync(context.Request.Query, ct).ConfigureAwait(false)));
        MapGet(app, "/containers/{id}/json", async (string id, WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.InspectContainerAsync(id, ct).ConfigureAwait(false)));
        MapGet(app, "/containers/{id}/logs", WriteLogsAsync);
        MapPost(app, "/containers/{id}/attach", AttachAsync);

        MapPost(app, "/containers/{id}/exec", (string id, DockerExecCreateRequest request,
            WslcDockerEngine engine) =>
        {
            var exec = engine.CreateExec(id, request);
            return Results.Json(new { exec.Id }, statusCode: StatusCodes.Status201Created);
        });
        MapPost(app, "/exec/{id}/start", StartExecAsync);
        MapGet(app, "/exec/{id}/json", (string id, WslcDockerEngine engine) =>
            Results.Json(engine.InspectExec(id)));

        MapDelete(app, "/containers/{id}", (string id, HttpContext context, WslcDockerEngine engine) =>
        {
            engine.DeleteContainer(id, DockerQuery.ReadBoolean(context.Request.Query, "force", false));
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        MapGet(app, "/volumes", async (WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ListVolumesAsync(ct).ConfigureAwait(false)));
        MapGet(app, "/volumes/{name}", async (string name, WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.InspectVolumeAsync(name, ct).ConfigureAwait(false)));

        MapGet(app, "/networks", async (WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.ListNetworksAsync(ct).ConfigureAwait(false)));
        MapGet(app, "/networks/{id}", async (string id, WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.InspectNetworkAsync(id, ct).ConfigureAwait(false)));
        MapPost(app, "/networks/create", () => DockerResults.Error(StatusCodes.Status501NotImplemented,
            "Custom Docker networks are not supported by the WSLC Docker socket yet."));
        MapDelete(app, "/networks/{id}", (string id) =>
            DockerResults.Error(StatusCodes.Status404NotFound, $"No such network: {id}"));

        MapGet(app, "/{**path}", DockerResults.NotImplemented);
        MapPost(app, "/{**path}", DockerResults.NotImplemented);
        MapDelete(app, "/{**path}", DockerResults.NotImplemented);
        MapPut(app, "/{**path}", DockerResults.NotImplemented);
        MapPatch(app, "/{**path}", DockerResults.NotImplemented);
    }

    private static async Task PullImageAsync(HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var image = DockerImageReference.FromPullQuery(context.Request.Query);
        await engine.PullImageAsync(image, context.Request.Headers["X-Registry-Auth"].ToString(), ct)
            .ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
                new { status = $"Downloaded newer image for {image.CanonicalName}" }, DockerJson.Options, ct)
            .ConfigureAwait(false);
        await context.Response.WriteAsync("\n", ct).ConfigureAwait(false);
    }

    private static async Task<IResult> WaitForContainerAsync(string id, WslcDockerEngine engine, CancellationToken ct)
    {
        var exitCode = await engine.WaitForContainerAsync(id, ct).ConfigureAwait(false);
        return Results.Json(new { StatusCode = exitCode, Error = new { Message = string.Empty } });
    }

    private static async Task WriteLogsAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var options = DockerStreamOptions.FromQuery(context.Request.Query);
        var follow = DockerQuery.ReadBoolean(context.Request.Query, "follow", false);
        var container = engine.GetContainer(id);
        if (!follow)
        {
            await DockerStreams.WriteFramesAsync(context.Response,
                    container.Output.Snapshot(options.IncludeStdout, options.IncludeStderr), ct)
                .ConfigureAwait(false);
            return;
        }

        using var subscription = container.Output.Subscribe(includeSnapshot: true, options.IncludeStdout,
            options.IncludeStderr);
        DockerStreams.ConfigureRawStreamResponse(context.Response);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        await foreach (var frame in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await DockerStreams.WriteChunkedFrameAsync(context.Response.Body, frame, ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        await DockerStreams.WriteChunkTerminatorAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private static async Task AttachAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var options = DockerStreamOptions.FromQuery(context.Request.Query);
        if (options.IncludeStdin)
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "Attach stdin is not supported by the WSLC Docker socket yet.");
        }

        var container = engine.GetContainer(id);
        if (!options.Stream)
        {
            await DockerStreams.WriteFramesAsync(context.Response,
                    options.Logs ? container.Output.Snapshot(options.IncludeStdout, options.IncludeStderr) : [], ct)
                .ConfigureAwait(false);
            return;
        }

        using var subscription = container.Output.Subscribe(options.Logs, options.IncludeStdout, options.IncludeStderr);
        DockerStreams.ConfigureRawStreamResponse(context.Response);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        await foreach (var frame in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await DockerStreams.WriteChunkedFrameAsync(context.Response.Body, frame, ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        await DockerStreams.WriteChunkTerminatorAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private static async Task StartExecAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var result = await engine.StartExecAsync(id, ct).ConfigureAwait(false);
        await DockerStreams.WriteFramesAsync(context.Response, result, ct).ConfigureAwait(false);
    }

    private static void MapGet(WebApplication app, string path, Delegate handler)
    {
        app.MapGet(path, handler);
        app.MapGet($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler);
    }

    private static void MapPost(WebApplication app, string path, Delegate handler)
    {
        app.MapPost(path, handler);
        app.MapPost($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler);
    }

    private static void MapDelete(WebApplication app, string path, Delegate handler)
    {
        app.MapDelete(path, handler);
        app.MapDelete($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler);
    }

    private static void MapPut(WebApplication app, string path, Delegate handler)
    {
        app.MapPut(path, handler);
        app.MapPut($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler);
    }

    private static void MapPatch(WebApplication app, string path, Delegate handler)
    {
        app.MapMethods(path, ["PATCH"], handler);
        app.MapMethods($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", ["PATCH"], handler);
    }
}

internal readonly record struct DockerStreamOptions(bool Logs, bool Stream, bool IncludeStdout, bool IncludeStderr, bool IncludeStdin)
{
    public static DockerStreamOptions FromQuery(IQueryCollection query) => new(
        DockerQuery.ReadBoolean(query, "logs", false),
        DockerQuery.ReadBoolean(query, "stream", false),
        DockerQuery.ReadBoolean(query, "stdout", true),
        DockerQuery.ReadBoolean(query, "stderr", true),
        DockerQuery.ReadBoolean(query, "stdin", false));
}

internal static class DockerQuery
{
    public static bool ReadBoolean(IQueryCollection query, string name, bool defaultValue)
    {
        var value = query[name].ToString();
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }

        if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new DockerApiException(StatusCodes.Status400BadRequest, $"Invalid boolean query parameter '{name}'.");
    }
}

internal static class DockerResults
{
    public static IResult Error(int statusCode, string message) => Results.Json(new { message }, statusCode: statusCode);

    public static IResult NotImplemented(HttpContext context) => Error(StatusCodes.Status404NotFound,
        $"endpoint not implemented: {context.Request.Method} {context.Request.Path}");

    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, CancellationToken ct)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new { message }, DockerJson.Options, ct)
            .ConfigureAwait(false);
    }
}
