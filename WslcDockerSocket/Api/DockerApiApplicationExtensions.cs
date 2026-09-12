namespace WslcDockerSocket.Api;

using System.Text.Json;
using Contracts;
using Engine;
using Streaming;

internal static partial class DockerApiApplicationExtensions
{
    public static IApplicationBuilder UseDockerApiExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
                if (context.Response.StatusCode >= 400) context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DockerApi").LogDockerApiRequestStatuscode(context.Request.Method, context.Request.Path, context.Response.StatusCode);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (exception is not DockerApiException) context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DockerApi").LogDockerApiRequestFailed(context.Request.Method, context.Request.Path, exception);
                await DockerResults.WriteErrorAsync(context, exception is DockerApiException dae ? dae.StatusCode : StatusCodes.Status500InternalServerError, exception.Message, context.RequestAborted).ConfigureAwait(false);
            }
        });
    }

    public static void MapDockerApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/", () => Results.Text("wslc-docker-socket", "text/plain"));
        MapGet(app, "/_ping", () => Results.Text("OK", "text/plain"));
        MapHead(app, "/_ping", () => Results.Ok());
        MapGet(app, "/version", () => Results.Json(WslcDockerEngine.GetVersion()));
        MapGet(app, "/info", async (WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.GetInfoAsync(ct).ConfigureAwait(false)));

        MapGet(app, "/images/json", async (WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.ListImagesAsync(ct).ConfigureAwait(false)));
        MapGet(app, "/images/{**image}", async (string image, HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
            image.EndsWith("/json", StringComparison.Ordinal) ? Results.Json(await engine.InspectImageAsync(image[..^"/json".Length], ct).ConfigureAwait(false)) : DockerResults.NotImplemented(context));
        MapPost(app, "/images/create", PullImageAsync);

        MapPost(app, "/containers/create", async (HttpContext context, DockerCreateContainerRequest request, WslcDockerEngine engine, CancellationToken ct) =>
        {
            var id = await engine.CreateContainerAsync(context.Request.Query["name"].ToString(), request, ct).ConfigureAwait(false);
            return Results.Json(new { Id = id, Warnings = Array.Empty<string>() }, statusCode: StatusCodes.Status201Created);
        });
        MapPost(app, "/containers/{id}/start", async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.StatusCode(await engine.StartContainerAsync(id, ct).ConfigureAwait(false) ? StatusCodes.Status204NoContent : StatusCodes.Status304NotModified));
        MapPost(app, "/containers/{id}/stop", async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.StatusCode(await engine.StopContainerAsync(id, ct).ConfigureAwait(false) ? StatusCodes.Status204NoContent : StatusCodes.Status304NotModified));
        MapPost(app, "/containers/{id}/wait", async (string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct) => await engine.WaitForContainerAsync(id, context.Response, ct).ConfigureAwait(false));
        MapGet(app, "/containers/json", async (HttpContext context, WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.ListContainersAsync(context.Request.Query, ct).ConfigureAwait(false)));
        MapGet(app, "/containers/{id}/json", async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.InspectContainerAsync(id, ct).ConfigureAwait(false)));
        MapGet(app, "/containers/{id}/logs", WriteLogsAsync);
        MapPut(app, "/containers/{id}/archive", CopyArchiveAsync);
        MapPost(app, "/containers/{id}/attach", (HttpContext context) =>
        {
            var options = DockerStreamOptions.FromQuery(context.Request.Query);
            return options.IncludeStdin
                ? DockerResults.Error(StatusCodes.Status501NotImplemented, "Attach stdin is not supported by the WSLC Docker socket yet.")
                : DockerResults.Error(StatusCodes.Status501NotImplemented, "Attach is not supported by the WSLC Docker socket yet.");
        });

        MapPost(app, "/containers/{id}/exec", async (string id, DockerExecCreateRequest request, WslcDockerEngine engine, CancellationToken ct) =>
        {
            var exec = await engine.CreateExecAsync(id, request, ct).ConfigureAwait(false);
            return Results.Json(new { exec.Id }, statusCode: StatusCodes.Status201Created);
        });
        MapPost(app, "/exec/{id}/start", StartExecAsync);
        MapGet(app, "/exec/{id}/json", (string id, WslcDockerEngine engine) => Results.Json(engine.InspectExec(id)));

        MapDelete(app, "/containers/{id}", async (string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
        {
            await engine.DeleteContainerAsync(id, DockerQuery.ReadBoolean(context.Request.Query, "force", false), ct).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });

        // WSLC exposes no event source, so Docker's event stream cannot be backed by real state transitions.
        MapGet(app, "/events", () => DockerResults.Error(StatusCodes.Status501NotImplemented, "Event streaming is not supported by the WSLC Docker socket because WSLC provides no event source."));

        MapGet(app, "/volumes", async (WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.ListVolumesAsync(ct).ConfigureAwait(false)));
        MapGet(app, "/volumes/{name}", async (string name, WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.InspectVolumeAsync(name, ct).ConfigureAwait(false)));
        MapGet(app, "/networks", async (WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.ListNetworksAsync(ct).ConfigureAwait(false)));
        MapGet(app, "/networks/{id}", async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.Json(await engine.InspectNetworkAsync(id, ct).ConfigureAwait(false)));
        MapPost(app, "/networks/create", () => DockerResults.Error(StatusCodes.Status501NotImplemented, "Custom Docker networks are not supported by the WSLC Docker socket yet."));
        MapDelete(app, "/networks/{id}", (string id) => DockerResults.Error(StatusCodes.Status404NotFound, $"No such network: {id}"));

        MapGet(app, "/{**path}", DockerResults.NotImplemented); MapPost(app, "/{**path}", DockerResults.NotImplemented); MapDelete(app, "/{**path}", DockerResults.NotImplemented); MapPut(app, "/{**path}", DockerResults.NotImplemented); MapPatch(app, "/{**path}", DockerResults.NotImplemented);
    }

    private static async Task CopyArchiveAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        if (DockerQuery.ReadBoolean(context.Request.Query, "copyUIDGID", false))
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "copyUIDGID is not supported by the WSLC Docker socket.");
        }

        if (DockerQuery.ReadBoolean(context.Request.Query, "noOverwriteDirNonDir", false))
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "noOverwriteDirNonDir is not supported by the WSLC Docker socket.");
        }

        await engine.CopyArchiveToContainerAsync(id, context.Request.Query["path"].ToString(), context.Request.Body, ct)
            .ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task PullImageAsync(HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var image = DockerImageReference.FromPullQuery(context.Request.Query);
        await engine.PullImageAsync(image, context.Request.Headers["X-Registry-Auth"].ToString(), ct).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new { status = $"Downloaded newer image for {image.CanonicalName}" }, DockerJson.Options, ct).ConfigureAwait(false);
        await context.Response.WriteAsync("\n", ct).ConfigureAwait(false);
    }

    private static async Task WriteLogsAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct)
    {
        var options = DockerStreamOptions.FromQuery(context.Request.Query);
        if (!DockerQuery.ReadBoolean(context.Request.Query, "follow", false))
        {
            var frames = await engine.GetLogsAsync(id, follow: false, ct).ConfigureAwait(false);
            await DockerStreams.WriteFramesAsync(context.Response, [.. frames.Where(frame => frame.Stream == DockerStreamType.Stdout ? options.IncludeStdout : options.IncludeStderr)], ct).ConfigureAwait(false);
            return;
        }

        DockerStreams.ConfigureRawStreamResponse(context.Response);
        await engine.StreamLogsAsync(id, async (frame, cancellationToken) =>
        {
            if (frame.Stream == DockerStreamType.Stdout ? options.IncludeStdout : options.IncludeStderr)
            {
                await DockerStreams.WriteChunkedFrameAsync(context.Response.Body, frame, cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        await DockerStreams.WriteChunkTerminatorAsync(context.Response.Body, ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task StartExecAsync(string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct) => await DockerStreams.WriteFramesAsync(context.Response, await engine.StartExecAsync(id, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private static void MapHead(WebApplication app, string path, Delegate handler) { app.MapMethods(path, ["HEAD"], handler); app.MapMethods($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", ["HEAD"], handler); }
    private static void MapGet(WebApplication app, string path, Delegate handler) { app.MapGet(path, handler); app.MapGet($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler); }
    private static void MapPost(WebApplication app, string path, Delegate handler) { app.MapPost(path, handler); app.MapPost($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler); }
    private static void MapDelete(WebApplication app, string path, Delegate handler) { app.MapDelete(path, handler); app.MapDelete($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler); }
    private static void MapPut(WebApplication app, string path, Delegate handler) { app.MapPut(path, handler); app.MapPut($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", handler); }
    private static void MapPatch(WebApplication app, string path, Delegate handler) { app.MapMethods(path, ["PATCH"], handler); app.MapMethods($"/v{{version:regex(^\\d+\\.\\d+$)}}{path}", ["PATCH"], handler); }

    [LoggerMessage(LogLevel.Error, "Docker API request {Method} {Path} failed.")] static partial void LogDockerApiRequestFailed(this ILogger logger, string method, PathString path, Exception exception);
    [LoggerMessage(LogLevel.Information, "Docker API request {Method} {Path} returned status code {StatusCode}.")] static partial void LogDockerApiRequestStatuscode(this ILogger logger, string method, PathString path, int statusCode);
}

internal readonly record struct DockerStreamOptions(bool Logs, bool Stream, bool IncludeStdout, bool IncludeStderr, bool IncludeStdin)
{
    public static DockerStreamOptions FromQuery(IQueryCollection query) => new(DockerQuery.ReadBoolean(query, "logs", false), DockerQuery.ReadBoolean(query, "stream", false), DockerQuery.ReadBoolean(query, "stdout", true), DockerQuery.ReadBoolean(query, "stderr", true), DockerQuery.ReadBoolean(query, "stdin", false));
}

internal static class DockerQuery
{
    public static bool ReadBoolean(IQueryCollection query, string name, bool defaultValue)
    {
        var value = query[name].ToString();
        if (string.IsNullOrEmpty(value)) return defaultValue;
        if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new DockerApiException(StatusCodes.Status400BadRequest, $"Invalid boolean query parameter '{name}'.");
    }
}

internal static class DockerResults
{
    public static IResult Error(int statusCode, string message) => Results.Json(new { message }, statusCode: statusCode);
    public static IResult NotImplemented(HttpContext context) => Error(StatusCodes.Status404NotFound, $"endpoint not implemented: {context.Request.Method} {context.Request.Path}");
    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, CancellationToken ct)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear(); context.Response.StatusCode = statusCode; context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new { message }, DockerJson.Options, ct).ConfigureAwait(false);
    }
}
