namespace WslcDockerSocket.Streaming;

using System.IO;
using System.Threading.Channels;
using Microsoft.WSL.Containers;
using Windows.Storage.Streams;
using WslcProcess = Microsoft.WSL.Containers.Process;

internal enum DockerStreamType : byte
{
    Stdout = 1,
    Stderr = 2,
}

internal readonly record struct DockerOutputFrame(DockerStreamType Stream, byte[] Data)
{
    public DockerOutputFrame Copy() => new(Stream, (byte[])Data.Clone());
}

internal static class DockerStreams
{
    public const string ContentType = "application/vnd.docker.raw-stream";

    public static IReadOnlyList<DockerOutputFrame> FromStdoutAndStderr(byte[] stdout, byte[] stderr)
    {
        var frames = new List<DockerOutputFrame>();
        if (stdout.Length > 0)
        {
            frames.Add(new DockerOutputFrame(DockerStreamType.Stdout, stdout));
        }

        if (stderr.Length > 0)
        {
            frames.Add(new DockerOutputFrame(DockerStreamType.Stderr, stderr));
        }

        return frames;
    }

    public static void ConfigureRawStreamResponse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        // Docker.DotNet's named-pipe transport treats Docker raw-stream content as chunked HTTP.
        // Kestrel does not apply chunk framing when this header is supplied directly, so this class
        // writes the framing explicitly before each Docker multiplex frame.
        response.Headers.TransferEncoding = "chunked";
    }

    public static async Task WriteFramesAsync(HttpResponse response, IReadOnlyList<DockerOutputFrame> frames, CancellationToken ct)
    {
        ConfigureRawStreamResponse(response);
        foreach (var frame in frames)
        {
            await WriteChunkedFrameAsync(response.Body, frame, ct).ConfigureAwait(false);
        }

        await WriteChunkTerminatorAsync(response.Body, ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task WriteChunkedFrameAsync(Stream destination, DockerOutputFrame frame, CancellationToken ct)
    {
        var length = checked(8 + frame.Data.Length);
        var header = System.Text.Encoding.ASCII.GetBytes(length.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
            + "\r\n");
        await destination.WriteAsync(header, ct).ConfigureAwait(false);
        await WriteFrameAsync(destination, frame, ct).ConfigureAwait(false);
        await destination.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    public static async Task WriteChunkTerminatorAsync(Stream destination, CancellationToken ct)
    {
        await destination.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    public static async Task WriteFrameAsync(Stream destination, DockerOutputFrame frame, CancellationToken ct)
    {
        var header = new byte[8];
        header[0] = (byte)frame.Stream;
        var length = checked((uint)frame.Data.Length);
        header[4] = (byte)(length >> 24);
        header[5] = (byte)(length >> 16);
        header[6] = (byte)(length >> 8);
        header[7] = (byte)length;
        await destination.WriteAsync(header, ct).ConfigureAwait(false);
        await destination.WriteAsync(frame.Data, ct).ConfigureAwait(false);
    }
}

internal sealed class DockerOutputBuffer
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumBufferedFramesPerSubscriber = 128;
    private readonly List<DockerOutputFrame> _frames = [];
    private readonly Dictionary<Guid, Subscriber> _subscribers = [];
    private readonly Lock _syncRoot = new();
    private int _bytes;
    private bool _completed;

    public void Append(DockerStreamType stream, byte[] data)
    {
        if (data is not { Length: > 0 })
        {
            return;
        }

        var frame = new DockerOutputFrame(stream, (byte[])data.Clone());
        lock (_syncRoot)
        {
            if (_completed)
            {
                return;
            }

            _frames.Add(frame);
            _bytes += frame.Data.Length;
            while (_bytes > MaximumBytes && _frames.Count > 0)
            {
                _bytes -= _frames[0].Data.Length;
                _frames.RemoveAt(0);
            }

            foreach (var (id, subscriber) in _subscribers.ToArray())
            {
                if (!subscriber.Accepts(frame))
                {
                    continue;
                }

                if (!subscriber.Channel.Writer.TryWrite(frame))
                {
                    subscriber.Channel.Writer.TryComplete(new IOException("Docker attach subscriber did not consume output fast enough."));
                    _subscribers.Remove(id);
                }
            }
        }
    }

    public IReadOnlyList<DockerOutputFrame> Snapshot(bool includeStdout = true, bool includeStderr = true)
    {
        lock (_syncRoot)
        {
            return [.. _frames.Where(frame => Includes(frame, includeStdout, includeStderr)).Select(frame => frame.Copy())];
        }
    }

    public DockerOutputSubscription Subscribe(bool includeSnapshot, bool includeStdout, bool includeStderr)
    {
        lock (_syncRoot)
        {
            var snapshot = includeSnapshot
                ? _frames.Where(frame => Includes(frame, includeStdout, includeStderr)).Select(frame => frame.Copy()).ToArray()
                : [];
            var channel = Channel.CreateBounded<DockerOutputFrame>(new BoundedChannelOptions(
                MaximumBufferedFramesPerSubscriber + snapshot.Length)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            foreach (var frame in snapshot)
            {
                if (!channel.Writer.TryWrite(frame))
                {
                    channel.Writer.TryComplete(new IOException("Docker attach snapshot exceeded the subscriber buffer."));
                    return new DockerOutputSubscription(Guid.Empty, channel.Reader, this);
                }
            }

            var id = Guid.NewGuid();
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(id, new Subscriber(channel, includeStdout, includeStderr));
            }

            return new DockerOutputSubscription(id, channel.Reader, this);
        }
    }

    public void Complete()
    {
        lock (_syncRoot)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    private static bool Includes(DockerOutputFrame frame, bool includeStdout, bool includeStderr) =>
        frame.Stream == DockerStreamType.Stdout ? includeStdout : includeStderr;

    private void Unsubscribe(Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_subscribers.Remove(id, out var subscriber))
            {
                subscriber.Channel.Writer.TryComplete();
            }
        }
    }

    private sealed class Subscriber(Channel<DockerOutputFrame> channel, bool includeStdout, bool includeStderr)
    {
        public Channel<DockerOutputFrame> Channel { get; } = channel;

        public bool Accepts(DockerOutputFrame frame) => Includes(frame, includeStdout, includeStderr);
    }

    internal sealed class DockerOutputSubscription(Guid id, ChannelReader<DockerOutputFrame> reader, DockerOutputBuffer owner)
        : IDisposable
    {
        public ChannelReader<DockerOutputFrame> Reader { get; } = reader;

        public void Dispose() => owner.Unsubscribe(id);
    }
}

internal static class DockerProcessOutput
{
    private const int BufferSize = 81920;
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(WslcProcess process, ProcessOutputHandle output, CancellationToken ct)
    {
        using var stream = process.GetOutputStream(output);
        using var memory = new MemoryStream();
        while (true)
        {
            var buffer = new Windows.Storage.Streams.Buffer(BufferSize);
            var operation = stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
            using var registration = ct.Register(operation.Cancel);
            IBuffer read;
            try
            {
                read = await operation;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (read.Length == 0)
            {
                break;
            }

            if (memory.Length + read.Length > MaximumBytes)
            {
                operation.Cancel();
                throw new IOException($"Docker exec output exceeds the {MaximumBytes} byte buffering limit.");
            }

            using var reader = DataReader.FromBuffer(read);
            var bytes = new byte[read.Length];
            reader.ReadBytes(bytes);
            await memory.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        return memory.ToArray();
    }
}
