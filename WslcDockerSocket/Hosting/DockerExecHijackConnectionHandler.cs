namespace WslcDockerSocket.Hosting;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Api;
using Engine;
using Microsoft.AspNetCore.Connections;
using Streaming;

/// <summary>
/// Handles Docker's HTTP/1.1 exec hijack before Kestrel's HTTP parser applies normal response framing.
/// </summary>
internal sealed class DockerExecHijackConnectionHandler(WslcDockerEngine engine)
{
    private static readonly byte[] HeaderTerminator = [.. "\r\n\r\n"u8];

    public async Task HandleAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        var input = connection.Transport.Input;
        while (true)
        {
            var readResult = await input.ReadAsync(connection.ConnectionClosed).ConfigureAwait(false);
            var buffer = readResult.Buffer;
            var request = TryReadRequest(buffer);
            switch (request.Kind)
            {
                case DockerExecHijackRequestKind.NeedMoreData when !readResult.IsCompleted:
                    input.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                case DockerExecHijackRequestKind.NeedMoreData:
                    input.AdvanceTo(buffer.Start, buffer.Start);
                    await next(connection).ConfigureAwait(false);
                    return;
                case DockerExecHijackRequestKind.PassThrough:
                    // Preserve the request for Kestrel and mark none of it examined so it reads it immediately.
                    input.AdvanceTo(buffer.Start, buffer.Start);
                    await next(connection).ConfigureAwait(false);
                    return;
                case DockerExecHijackRequestKind.Hijack:
                    input.AdvanceTo(buffer.GetPosition(request.Length), buffer.GetPosition(request.Length));
                    try
                    {
                        engine.EnsureExecExists(request.ExecId!);
                        await WriteHijackedExecAsync(connection, request.ExecId!, connection.ConnectionClosed).ConfigureAwait(false);
                    }
                    catch (DockerApiException exception)
                    {
                        await WriteDockerErrorAsync(connection, exception, connection.ConnectionClosed).ConfigureAwait(false);
                    }
                    finally
                    {
                        await connection.Transport.Output.CompleteAsync().ConfigureAwait(false);
                    }

                    return;
                default:
                    throw new InvalidOperationException("Unknown Docker exec hijack request state.");
            }
        }
    }

    private async Task WriteHijackedExecAsync(ConnectionContext connection, string execId, CancellationToken ct)
    {
        var output = connection.Transport.Output;
        await output.WriteAsync(
                "HTTP/1.1 200 OK\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n"u8.ToArray(),
                ct)
            .ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        var frames = await engine.StartExecAsync(execId, ct).ConfigureAwait(false);
        await using var stream = output.AsStream(leaveOpen: true);
        foreach (var frame in frames)
        {
            await DockerStreams.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteDockerErrorAsync(ConnectionContext connection, DockerApiException exception,
        CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { message = exception.Message });
        var header = $"HTTP/1.1 {exception.StatusCode.ToString(CultureInfo.InvariantCulture)} Error\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n";
        var output = connection.Transport.Output;
        await output.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static DockerExecHijackRequest TryReadRequest(ReadOnlySequence<byte> buffer)
    {
        var data = buffer.ToArray();
        var headerEnd = data.AsSpan().IndexOf(HeaderTerminator);
        if (headerEnd < 0)
        {
            return DockerExecHijackRequest.NeedMoreData;
        }

        var header = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = header.Split("\r\n");
        if (lines.Length == 0 || !TryGetExecId(lines[0], out var execId))
        {
            return DockerExecHijackRequest.PassThrough;
        }

        var contentLength = 0;
        var hasUpgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(value, out contentLength) || contentLength < 0))
            {
                return DockerExecHijackRequest.PassThrough;
            }

            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && value.Split(',', StringSplitOptions.TrimEntries)
                    .Any(token => token.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)))
            {
                hasUpgrade = true;
            }
        }

        if (!hasUpgrade)
        {
            return DockerExecHijackRequest.PassThrough;
        }

        var requestLength = checked(headerEnd + HeaderTerminator.Length + contentLength);
        return data.Length < requestLength
            ? DockerExecHijackRequest.NeedMoreData
            : new DockerExecHijackRequest(DockerExecHijackRequestKind.Hijack, requestLength, execId);
    }

    private static bool TryGetExecId(string requestLine, out string? execId)
    {
        execId = null;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].Equals("POST", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = parts[1].Split('?', 2)[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var offset = segments.Length == 4 && segments[0].StartsWith('v') ? 1 : 0;
        if (segments.Length - offset != 3 || !segments[offset].Equals("exec", StringComparison.OrdinalIgnoreCase)
            || !segments[offset + 2].Equals("start", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[offset + 1]))
        {
            return false;
        }

        execId = Uri.UnescapeDataString(segments[offset + 1]);
        return true;
    }

    private enum DockerExecHijackRequestKind
    {
        NeedMoreData,
        PassThrough,
        Hijack,
    }

    private readonly record struct DockerExecHijackRequest(DockerExecHijackRequestKind Kind, long Length, string? ExecId)
    {
        public static DockerExecHijackRequest NeedMoreData { get; } = new(DockerExecHijackRequestKind.NeedMoreData, 0, null);

        public static DockerExecHijackRequest PassThrough { get; } = new(DockerExecHijackRequestKind.PassThrough, 0, null);
    }
}
