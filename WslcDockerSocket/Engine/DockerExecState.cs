namespace WslcDockerSocket.Engine;

using System.Text;

// This state is transient metadata for one Docker exec request, never container authority.
internal sealed class DockerExecState(string id, string containerId, IReadOnlyList<string> command)
{
    private readonly Lock _sync = new();
    private Task<WslcCommandResult>? _operation;

    public string Id { get; } = id;
    public string ContainerId { get; } = containerId;
    public IReadOnlyList<string> Command { get; } = command;
    public bool Running { get; private set; }
    public int? ExitCode { get; private set; }

    public async Task<DockerExecResult> StartAsync(IWslcCommandRunner runner, CancellationToken ct)
    {
        Task<WslcCommandResult> operation;
        lock (_sync)
        {
            if (_operation is not null)
            {
                operation = _operation;
            }
            else
            {
                Running = true;
                _operation = runner.RunAsync(["container", "exec", ContainerId, .. Command], ct);
                operation = _operation;
            }
        }

        var result = await operation.ConfigureAwait(false);
        lock (_sync)
        {
            Running = false;
            ExitCode = result.ExitCode;
        }

        return new DockerExecResult(Encoding.UTF8.GetBytes(result.StandardOutput), Encoding.UTF8.GetBytes(result.StandardError));
    }
}

internal readonly record struct DockerExecResult(byte[] StandardOutputBytes, byte[] StandardErrorBytes);
