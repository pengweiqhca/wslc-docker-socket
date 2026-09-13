
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using WslcDockerSocket.Api;
using WslcDockerSocket.Streaming;

namespace WslcDockerSocket.Engine;

internal interface IWslcCommandRunner
{
    Task<WslcCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct);

    Task<WslcCommandResult> RunWithStandardInputAsync(IReadOnlyList<string> command, Stream input, CancellationToken ct);

    Task StreamAsync(IReadOnlyList<string> command, Func<DockerOutputFrame, CancellationToken, ValueTask> writeFrameAsync,
        CancellationToken ct);
}

internal readonly record struct WslcCommandResult(string StandardOutput, string StandardError, int ExitCode);

internal sealed class WslcCommandRunner(string? session = null) : IWslcCommandRunner
{
    private const string ExecutablePath = "wslc";
    // wslc reports a missing session by name; it selects an existing session and never creates one.
    private const string SessionNotFound = "WSLC_E_SESSION_NOT_FOUND";
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;
    private const int StreamingBufferBytes = 81920;

    // wslc's session RPC channel does not tolerate concurrent invocations reliably (observed as a raw
    // RPC_E_DISCONNECTED failure from wslc itself), so one-shot commands are serialized process-wide. This
    // matters once a container's own Docker client (e.g. Testcontainers' Ryuk over DOCKER_HOST) can call back
    // into this engine while the original request that created it is still running.
    private static readonly SemaphoreSlim WslcSessionGate = new(1, 1);

    public async Task<WslcCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await WslcSessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ExecuteAsync(BuildArguments(command, session), ct).ConfigureAwait(false);
            if (!IndicatesMissingSession(result)) return result;

            // wslc only ever selects an existing session, so the configured one is brought up by running a
            // read-only command without it first. The requested command is never retried unqualified: a
            // mutation would then run in the wrong session.
            await ExecuteAsync(["container", "list", "--format", "json"], ct).ConfigureAwait(false);
            return await ExecuteAsync(BuildArguments(command, session), ct).ConfigureAwait(false);
        }
        finally
        {
            WslcSessionGate.Release();
        }
    }

    private bool IndicatesMissingSession(WslcCommandResult result) =>
        !string.IsNullOrWhiteSpace(session)
        && result.ExitCode != 0
        && result.StandardError.Contains(SessionNotFound, StringComparison.OrdinalIgnoreCase);

    private static async Task<WslcCommandResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        {
            using var process = Start(arguments);
            var standardOutput = ReadBoundedAsync(process.StandardOutput);
            var standardError = ReadBoundedAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                throw;
            }

            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (output.ExceededLimit || error.ExceededLimit)
            {
                throw new DockerApiException(StatusCodes.Status500InternalServerError,
                    $"wslc command output exceeds the {MaximumOutputCharacters} character buffering limit.");
            }

            return new WslcCommandResult(output.Value, error.Value, process.ExitCode);
        }
    }

    public async Task<WslcCommandResult> RunWithStandardInputAsync(IReadOnlyList<string> command, Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(input);
        await WslcSessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var process = Start(BuildArguments(command, session), redirectStandardInput: true);
            var standardOutput = ReadBoundedAsync(process.StandardOutput);
            var standardError = ReadBoundedAsync(process.StandardError);
            try
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                throw;
            }
            catch
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
                throw;
            }

            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (output.ExceededLimit || error.ExceededLimit)
            {
                throw new DockerApiException(StatusCodes.Status500InternalServerError,
                    $"wslc command output exceeds the {MaximumOutputCharacters} character buffering limit.");
            }

            return new WslcCommandResult(output.Value, error.Value, process.ExitCode);
        }
        finally
        {
            WslcSessionGate.Release();
        }
    }

    public async Task StreamAsync(IReadOnlyList<string> command,
        Func<DockerOutputFrame, CancellationToken, ValueTask> writeFrameAsync, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(writeFrameAsync);
        using var process = Start(BuildArguments(command, session));
        using var writeGate = new SemaphoreSlim(1, 1);
        var forwardingFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var standardOutput = PumpAsync(process.StandardOutput.BaseStream, DockerStreamType.Stdout, writeFrameAsync,
            writeGate, forwardingFailure, ct);
        var standardError = PumpAsync(process.StandardError.BaseStream, DockerStreamType.Stderr, writeFrameAsync,
            writeGate, forwardingFailure, ct);
        var processExited = process.WaitForExitAsync(CancellationToken.None);
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, ct);

        if (await Task.WhenAny(processExited, forwardingFailure.Task, cancellation).ConfigureAwait(false) != processExited)
        {
            Kill(process);
        }

        await processExited.ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (forwardingFailure.Task.IsCompleted)
        {
            ExceptionDispatchInfo.Capture(await forwardingFailure.Task.ConfigureAwait(false)).Throw();
        }

        if (process.ExitCode != 0)
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"wslc {string.Join(' ', command)} failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task PumpAsync(Stream source, DockerStreamType stream,
        Func<DockerOutputFrame, CancellationToken, ValueTask> writeFrameAsync, SemaphoreSlim writeGate,
        TaskCompletionSource<Exception> forwardingFailure, CancellationToken ct)
    {
        var buffer = new byte[StreamingBufferBytes];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                if (ct.IsCancellationRequested || forwardingFailure.Task.IsCompleted)
                {
                    continue;
                }

                var lockTaken = false;
                try
                {
                    await writeGate.WaitAsync(ct).ConfigureAwait(false);
                    lockTaken = true;
                    if (!ct.IsCancellationRequested && !forwardingFailure.Task.IsCompleted)
                    {
                        await writeFrameAsync(new DockerOutputFrame(stream, buffer[..read].ToArray()), ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The parent kills the child; this pump continues draining both pipes to EOF.
                }
                catch (Exception exception)
                {
                    forwardingFailure.TrySetResult(exception);
                }
                finally
                {
                    if (lockTaken)
                    {
                        writeGate.Release();
                    }
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || forwardingFailure.Task.IsCompleted)
        {
            // Killing the child while cancelling can close its pipes while this pump drains them.
        }
        catch (Exception exception)
        {
            forwardingFailure.TrySetResult(exception);
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[81920];
        var result = new StringBuilder();
        var exceededLimit = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                return new BoundedOutput(result.ToString(), exceededLimit);
            }

            if (result.Length <= MaximumOutputCharacters - read)
            {
                result.Append(buffer, 0, read);
            }
            else
            {
                exceededLimit = true;
            }
        }
    }

    private static Process Start(IReadOnlyList<string> command, bool redirectStandardInput = false)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(command, redirectStandardInput),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new DockerApiException(StatusCodes.Status500InternalServerError, "Failed to start wslc.");
            }

            return process;
        }
        catch (Win32Exception exception)
        {
            process.Dispose();
            throw new DockerApiException(StatusCodes.Status503ServiceUnavailable,
                $"Unable to start '{ExecutablePath}': {exception.Message}");
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and Kill.
        }
    }

    /// <summary>
    /// Prefixes the configured session. <c>--session</c> is a global option, so wslc only accepts it before the
    /// subcommand. Selecting a session explicitly keeps the adapter on one session regardless of whether the
    /// process is elevated, because wslc otherwise derives a separate session name for an elevated token.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> command, string? session) =>
        string.IsNullOrWhiteSpace(session) ? command : ["--session", session, .. command];

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> command, bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private readonly record struct BoundedOutput(string Value, bool ExceededLimit);
}
