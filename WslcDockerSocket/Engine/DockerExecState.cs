namespace WslcDockerSocket.Engine;

using Api.Contracts;
using Microsoft.WSL.Containers;
using Streaming;

internal sealed class DockerExecState(string id, DockerContainerState container, DockerExecCreateRequest request)
{
    private readonly TaskCompletionSource<IReadOnlyList<DockerOutputFrame>> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    public string Id { get; } = id;

    public string ContainerId { get; } = container.Id;

    public IReadOnlyList<string> Command { get; } = request.Cmd ?? [];

    public bool Running { get; private set; }

    public int ExitCode { get; private set; }

    public async Task<IReadOnlyList<DockerOutputFrame>> StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return await _result.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        Running = true;
        using var process = container.CreateProcess(request);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(int exitCode) => exited.TrySetResult(exitCode);
        process.Exited += OnExited;
        try
        {
            process.Start();
            var stdout = DockerProcessOutput.ReadAsync(process, ProcessOutputHandle.StandardOutput, ct);
            var stderr = DockerProcessOutput.ReadAsync(process, ProcessOutputHandle.StandardError, ct);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            ExitCode = process.State is ProcessState.Exited or ProcessState.Signalled
                ? process.ExitCode
                : await exited.Task.WaitAsync(ct).ConfigureAwait(false);
            var output = DockerStreams.FromStdoutAndStderr(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            _result.TrySetResult(output);
            return output;
        }
        catch (Exception exception)
        {
            _result.TrySetException(exception);
            throw;
        }
        finally
        {
            process.Exited -= OnExited;
            Running = false;
        }
    }
}
