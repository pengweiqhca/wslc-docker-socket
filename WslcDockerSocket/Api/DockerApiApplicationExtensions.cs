namespace WslcDockerSocket.Api;

using System.Text.Json;
using Contracts;
using Engine;
using Streaming;

internal static partial class DockerApiApplicationExtensions
{
    private const string ImageHistoryUnsupported =
        "Image history is not supported by the WSLC Docker socket because WSLC reports no per-layer build history.";

    private const string TtyResizeUnsupported =
        "TTY resize is not supported by the WSLC Docker socket because containers and execs run without a TTY.";

    public static IApplicationBuilder UseDockerApiExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
#if DEBUG
                if (context.Response.StatusCode >= 400)
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DockerApi")
                        .LogDockerApiRequestStatusCode(context.Request.Method, context.Request.FullPath(),
                            context.Response.StatusCode);
#endif
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (exception is not DockerApiException)
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DockerApi")
                        .LogDockerApiRequestFailed(context.Request.Method, context.Request.FullPath(), exception);
                await DockerResults.WriteErrorAsync(context,
                    exception is DockerApiException dae ? dae.StatusCode : StatusCodes.Status500InternalServerError,
                    exception.Message, context.RequestAborted).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Moves Docker's optional <c>/v{major}.{minor}</c> API prefix into <see cref="HttpRequest.PathBase"/> so each
    /// endpoint is registered once. Must run before routing, otherwise matching sees the versioned path.
    /// </summary>
    public static IApplicationBuilder UseDockerApiVersionPathBase(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (TryGetVersionPrefix(context.Request.Path, out var prefix)
                && context.Request.Path.StartsWithSegments(prefix, out var remaining))
            {
                context.Request.PathBase = context.Request.PathBase.Add(prefix);
                context.Request.Path = remaining.HasValue ? remaining : "/";
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool TryGetVersionPrefix(PathString path, out PathString prefix)
    {
        prefix = default;
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value[0] != '/') return false;
        var end = value.IndexOf('/', 1);
        if (!IsDockerApiVersion(end < 0 ? value.AsSpan(1) : value.AsSpan(1, end - 1))) return false;
        prefix = new PathString(end < 0 ? value : value[..end]);
        return true;
    }

    /// <summary>Matches Docker's version segment shape that per-route regex constraints previously enforced.</summary>
    private static bool IsDockerApiVersion(ReadOnlySpan<char> segment)
    {
        if (segment.Length < 4 || (segment[0] != 'v' && segment[0] != 'V')) return false;
        var version = segment[1..];
        var separator = version.IndexOf('.');
        return separator > 0 && separator < version.Length - 1
            && IsAsciiDigits(version[..separator]) && IsAsciiDigits(version[(separator + 1)..]);
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character)) return false;
        }

        return true;
    }

    public static void MapDockerApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/", () => Results.Text("wslc-docker-socket", "text/plain"));
        app.MapGet("/_ping", () => Results.Text("OK", "text/plain"));
        app.MapMethods("/_ping", ["HEAD"], () => Results.Ok());
        app.MapGet("/version", () => Results.Json(WslcDockerEngine.GetVersion()));
        app.MapGet("/info",
            async (WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.GetInfoAsync(ct).ConfigureAwait(false)));

        app.MapGet("/images/json",
            async (WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.ListImagesAsync(ct).ConfigureAwait(false)));
        // Repository names may contain slashes, so image subresources are matched on the catch-all segment.
        app.MapGet("/images/{**image}",
            async (string image, HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
                image.EndsWith("/json", StringComparison.Ordinal)
                    ? Results.Json(await engine.InspectImageAsync(image[..^"/json".Length], ct).ConfigureAwait(false))
                    : image.EndsWith("/history", StringComparison.Ordinal)
                        ? DockerResults.Error(StatusCodes.Status501NotImplemented, ImageHistoryUnsupported)
                        : DockerResults.NotImplemented(context));
        app.MapPost("/images/create", PullImageAsync);

        app.MapPost("/containers/create", async (HttpContext context, DockerCreateContainerRequest request,
            WslcDockerEngine engine, CancellationToken ct) => Results.Json(new
        {
            Id = await engine.CreateContainerAsync(context.Request.Query["name"].ToString(), request, ct)
                .ConfigureAwait(false),
            Warnings = Array.Empty<string>()
        }, statusCode: StatusCodes.Status201Created));

        app.MapPost("/containers/{id}/start",
            async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.StatusCode(
                await engine.StartContainerAsync(id, ct).ConfigureAwait(false)
                    ? StatusCodes.Status204NoContent
                    : StatusCodes.Status304NotModified));
        app.MapPost("/containers/{id}/stop",
            async (string id, WslcDockerEngine engine, CancellationToken ct) => Results.StatusCode(
                await engine.StopContainerAsync(id, ct).ConfigureAwait(false)
                    ? StatusCodes.Status204NoContent
                    : StatusCodes.Status304NotModified));
        app.MapPost("/containers/{id}/wait",
            async (string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
                await engine.WaitForContainerAsync(id, context.Response, ct).ConfigureAwait(false));
        app.MapGet("/containers/json",
            async (HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.ListContainersAsync(context.Request.Query, ct).ConfigureAwait(false)));
        app.MapGet("/containers/{id}/json", async (string id, WslcDockerEngine engine, CancellationToken ct) =>
            Results.Json(await engine.InspectContainerAsync(id, ct).ConfigureAwait(false)));
        app.MapGet("/containers/{id}/logs", WriteLogsAsync);
        app.MapPut("/containers/{id}/archive", CopyArchiveAsync);
        app.MapPost("/containers/{id}/attach", (HttpContext context) =>
            DockerStreamOptions.FromQuery(context.Request.Query).IncludeStdin
                ? DockerResults.Error(StatusCodes.Status501NotImplemented,
                    "Attach stdin is not supported by the WSLC Docker socket yet.")
                : DockerResults.Error(StatusCodes.Status501NotImplemented,
                    "Attach is not supported by the WSLC Docker socket yet."));

        // Containers reject Tty and execs run without one, so there is no TTY whose dimensions could change.
        app.MapPost("/containers/{id}/resize", () => DockerResults.Error(StatusCodes.Status501NotImplemented, TtyResizeUnsupported));
        app.MapPost("/exec/{id}/resize", () => DockerResults.Error(StatusCodes.Status501NotImplemented, TtyResizeUnsupported));

        app.MapPost("/containers/{id}/exec",
            async (string id, DockerExecCreateRequest request, WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(new { (await engine.CreateExecAsync(id, request, ct).ConfigureAwait(false)).Id },
                    statusCode: StatusCodes.Status201Created));
        app.MapPost("/exec/{id}/start", StartExecAsync);
        app.MapGet("/exec/{id}/json", (string id, WslcDockerEngine engine) => Results.Json(engine.InspectExec(id)));

        app.MapDelete("/containers/{id}",
            async (string id, HttpContext context, WslcDockerEngine engine, CancellationToken ct) =>
            {
                await engine
                    .DeleteContainerAsync(id, DockerQuery.ReadBoolean(context.Request.Query, "force", false), ct)
                    .ConfigureAwait(false);
                return Results.StatusCode(StatusCodes.Status204NoContent);
            });

        // WSLC exposes no event source, so Docker's event stream cannot be backed by real state transitions.
        app.MapGet("/events",
            () => DockerResults.Error(StatusCodes.Status501NotImplemented,
                "Event streaming is not supported by the WSLC Docker socket because WSLC provides no event source."));

        app.MapGet("/volumes",
            async (WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.ListVolumesAsync(ct).ConfigureAwait(false)));
        app.MapGet("/volumes/{name}",
            async (string name, WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.InspectVolumeAsync(name, ct).ConfigureAwait(false)));
        app.MapGet("/networks",
            async (WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.ListNetworksAsync(ct).ConfigureAwait(false)));
        app.MapGet("/networks/{id}",
            async (string id, WslcDockerEngine engine, CancellationToken ct) =>
                Results.Json(await engine.InspectNetworkAsync(id, ct).ConfigureAwait(false)));
        app.MapPost("/networks/create",
            () => DockerResults.Error(StatusCodes.Status501NotImplemented,
                "Custom Docker networks are not supported by the WSLC Docker socket yet."));
        app.MapDelete("/networks/{id}",
            (string id) => DockerResults.Error(StatusCodes.Status404NotFound, $"No such network: {id}"));

        app.MapGet("/{**path}", DockerResults.NotImplemented);
        app.MapPost("/{**path}", DockerResults.NotImplemented);
        app.MapDelete("/{**path}", DockerResults.NotImplemented);
        app.MapPut("/{**path}", DockerResults.NotImplemented);
        app.MapMethods("/{**path}", ["PATCH"], DockerResults.NotImplemented);
    }

    private static async Task CopyArchiveAsync(string id, HttpContext context, WslcDockerEngine engine,
        CancellationToken ct)
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
        await engine.PullImageAsync(image, context.Request.Headers["X-Registry-Auth"].ToString(), ct)
            .ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body,
                new { status = $"Downloaded newer image for {image.CanonicalName}" }, DockerJson.Options, ct)
            .ConfigureAwait(false);
        await context.Response.WriteAsync("\n", ct).ConfigureAwait(false);
    }

    private static async Task WriteLogsAsync(string id, HttpContext context, WslcDockerEngine engine,
        CancellationToken ct)
    {
        var options = DockerStreamOptions.FromQuery(context.Request.Query);
        if (!DockerQuery.ReadBoolean(context.Request.Query, "follow", false))
        {
            var frames = await engine.GetLogsAsync(id, follow: false, ct).ConfigureAwait(false);
            await DockerStreams.WriteFramesAsync(context.Response,
            [
                .. frames.Where(frame =>
                    frame.Stream == DockerStreamType.Stdout ? options.IncludeStdout : options.IncludeStderr)
            ], ct).ConfigureAwait(false);
            return;
        }

        DockerStreams.ConfigureRawStreamResponse(context.Response);
        await engine.StreamLogsAsync(id, async (frame, cancellationToken) =>
        {
            if (frame.Stream == DockerStreamType.Stdout ? options.IncludeStdout : options.IncludeStderr)
            {
                await DockerStreams.WriteChunkedFrameAsync(context.Response.Body, frame, cancellationToken)
                    .ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        await DockerStreams.WriteChunkTerminatorAsync(context.Response.Body, ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task StartExecAsync(string id, HttpContext context, WslcDockerEngine engine,
        CancellationToken ct) => await DockerStreams
        .WriteFramesAsync(context.Response, await engine.StartExecAsync(id, ct).ConfigureAwait(false), ct)
        .ConfigureAwait(false);

    [LoggerMessage(LogLevel.Error, "Docker API request {Method} {Path} failed.")]
    static partial void LogDockerApiRequestFailed(this ILogger logger, string method, string path,
        Exception exception);

    [LoggerMessage(LogLevel.Information, "Docker API request {Method} {Path} returned status code {StatusCode}.")]
    static partial void LogDockerApiRequestStatusCode(this ILogger logger, string method, string path,
        int statusCode);
}

internal readonly record struct DockerStreamOptions(
    bool Logs,
    bool Stream,
    bool IncludeStdout,
    bool IncludeStderr,
    bool IncludeStdin)
{
    public static DockerStreamOptions FromQuery(IQueryCollection query) => new(
        DockerQuery.ReadBoolean(query, "logs", false), DockerQuery.ReadBoolean(query, "stream", false),
        DockerQuery.ReadBoolean(query, "stdout", true), DockerQuery.ReadBoolean(query, "stderr", true),
        DockerQuery.ReadBoolean(query, "stdin", false));
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

internal static class DockerRequestExtensions
{
    /// <summary>Restores the client's original path, including the API version moved into PathBase.</summary>
    public static string FullPath(this HttpRequest request) => $"{request.PathBase}{request.Path}";
}

internal static class DockerResults
{
    public static IResult Error(int statusCode, string message) =>
        Results.Json(new { message }, statusCode: statusCode);

    public static IResult NotImplemented(HttpContext context) => Error(StatusCodes.Status404NotFound,
        $"endpoint not implemented: {context.Request.Method} {context.Request.FullPath()}");

    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, CancellationToken ct)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new { message }, DockerJson.Options, ct)
            .ConfigureAwait(false);
    }
}
