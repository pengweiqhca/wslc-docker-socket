namespace WslcDockerSocket.Engine;

using System.Globalization;
using Api;
using Api.Contracts;
using Microsoft.WSL.Containers;
using Streaming;
using WslcContainer = Microsoft.WSL.Containers.Container;
using WslcContainerState = Microsoft.WSL.Containers.ContainerState;
using WslcProcess = Microsoft.WSL.Containers.Process;

internal sealed class DockerContainerState : IDisposable
{
    private readonly WslcContainer _container;
    private readonly WslcProcess _initProcess;
    private readonly Lock _syncRoot = new();
    private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<DockerPortBinding> _requestedPortBindings;
    private readonly Dictionary<DockerPortKey, ushort> _mappedPorts = [];
    private int _disposed;
    private int _exitCodeValue;
    private bool _hasExited;

    public DockerContainerState(
        string id,
        string name,
        DockerImageReference image,
        DockerCreateContainerRequest request,
        WslcContainer container,
        IReadOnlyList<DockerPortBinding> requestedPortBindings)
    {
        Id = id;
        Name = name;
        Image = image;
        Request = request;
        _container = container;
        _initProcess = container.InitProcess;
        _requestedPortBindings = requestedPortBindings;
        CreatedAt = DateTimeOffset.UtcNow;
        Output = new DockerOutputBuffer();
        _initProcess.OutputReceived += OnOutput;
        _initProcess.ErrorReceived += OnError;
        _initProcess.Exited += OnExited;
    }

    public string Id { get; }

    public string Name { get; }

    public DockerImageReference Image { get; }

    public DockerCreateContainerRequest Request { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public DockerOutputBuffer Output { get; }

    public string State
    {
        get
        {
            if (_hasExited)
            {
                return "exited";
            }

            return _container.State switch
            {
                WslcContainerState.Created => "created",
                WslcContainerState.Running => "running",
                WslcContainerState.Exited => "exited",
                _ => "exited",
            };
        }
    }

    public bool Matches(string idOrName) => Id.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(Name, idOrName.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        _container.Start();
        StartedAt ??= DateTimeOffset.UtcNow;
        RefreshMappedPorts();
    }

    public void Stop()
    {
        if (State == "running")
        {
            _container.Stop(Signal.SIGTERM, TimeSpan.FromSeconds(10));
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        if (_hasExited)
        {
            return _exitCodeValue;
        }

        return await _exitCode.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Delete(bool force)
    {
        if (State == "running" && !force)
        {
            throw new DockerApiException(StatusCodes.Status409Conflict,
                $"Conflict. The container {Id} is running and must be stopped before removal.");
        }

        if (_container.State is not WslcContainerState.Deleted and not WslcContainerState.Invalid)
        {
            _container.Delete(DeleteContainerOption.Force);
        }

        Output.Complete();
    }

    public WslcProcess CreateProcess(DockerExecCreateRequest request)
    {
        var settings = new ProcessSettings
        {
            CommandLine = request.Cmd,
            OutputMode = ProcessOutputMode.Stream,
            EnvironmentVariables = DockerEnvironment.Parse(request.Env),
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDir))
        {
            settings.WorkingDirectory = request.WorkingDir;
        }

        return _container.CreateProcess(settings);
    }

    public object ToInspectResponse()
    {
        RefreshMappedPorts();
        var status = State;
        var isRunning = status == "running";
        var hasExited = status == "exited";
        var command = DockerCommand.Combine(Request.Entrypoint, Request.Cmd);
        var ports = DockerPortBinding.ToDockerResponse(_requestedPortBindings, _mappedPorts);
        return new
        {
            Id,
            Name = "/" + Name,
            Created = CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            Path = command.Count > 0 ? command[0] : string.Empty,
            Args = command.Count > 1 ? command.Skip(1) : [],
            State = new
            {
                Status = status,
                Running = isRunning,
                Paused = false,
                Restarting = false,
                OOMKilled = false,
                Dead = false,
                Pid = 0,
                ExitCode = hasExited ? _exitCodeValue : 0,
                Error = string.Empty,
                StartedAt = StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "0001-01-01T00:00:00.0000000Z",
                FinishedAt = FinishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "0001-01-01T00:00:00.0000000Z",
            },
            Config = new
            {
                Hostname = Request.Hostname ?? Name,
                Image = Image.CanonicalName,
                Env = Request.Env ?? [],
                Cmd = Request.Cmd ?? [],
                Entrypoint = Request.Entrypoint ?? [],
                WorkingDir = Request.WorkingDir ?? string.Empty,
                Request.Tty,
                Labels = Request.Labels ?? [],
                ExposedPorts = Request.ExposedPorts ?? [],
            },
            HostConfig = new
            {
                AutoRemove = Request.HostConfig?.AutoRemove ?? false,
                Privileged = Request.HostConfig?.Privileged ?? false,
                NetworkMode = Request.HostConfig?.NetworkMode ?? "default",
                PortBindings = ports,
            },
            NetworkSettings = new
            {
                Bridge = string.Empty,
                SandboxID = Id,
                HairpinMode = false,
                LinkLocalIPv6Address = string.Empty,
                LinkLocalIPv6PrefixLen = 0,
                Ports = ports,
                SandboxKey = string.Empty,
                SecondaryIPAddresses = Array.Empty<object>(),
                SecondaryIPv6Addresses = Array.Empty<object>(),
                EndpointID = string.Empty,
                Gateway = string.Empty,
                GlobalIPv6Address = string.Empty,
                GlobalIPv6PrefixLen = 0,
                IPAddress = string.Empty,
                IPPrefixLen = 0,
                IPv6Gateway = string.Empty,
                MacAddress = string.Empty,
                Networks = new Dictionary<string, object>(),
            },
        };
    }

    public object ToListResponse()
    {
        RefreshMappedPorts();
        return new
        {
            Id,
            Names = new[] { "/" + Name },
            Image = Image.CanonicalName,
            ImageID = "wslc:" + DockerId.From(Image.CanonicalName),
            Command = string.Join(" ", DockerCommand.Combine(Request.Entrypoint, Request.Cmd)),
            Created = CreatedAt.ToUnixTimeSeconds(),
            State,
            Status = State == "running" ? "Up" : $"Exited ({_exitCodeValue})",
            Labels = Request.Labels ?? [],
            Ports = DockerPortBinding.ToListResponse(_requestedPortBindings, _mappedPorts),
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Delete(force: true);
        }
        catch
        {
            // Shutdown and deletion are best-effort; the owning session is terminated afterwards.
        }

        _initProcess.OutputReceived -= OnOutput;
        _initProcess.ErrorReceived -= OnError;
        _initProcess.Exited -= OnExited;
        _initProcess.Dispose();
        _container.Dispose();
        Output.Complete();
    }

    private void RefreshMappedPorts()
    {
        if (_requestedPortBindings.Count == 0 || State is "created" or "exited")
        {
            return;
        }

        try
        {
            var mappings = DockerPortBinding.ParseRuntimeInspect(_container.Inspect());
            lock (_syncRoot)
            {
                foreach (var binding in _requestedPortBindings)
                {
                    if (mappings.TryGetValue(binding.Key, out var hostPort))
                    {
                        _mappedPorts[binding.Key] = hostPort;
                    }
                }
            }
        }
        catch
        {
            // A failed WSLC inspect must not fabricate a Docker port binding from requested configuration.
        }
    }

    private void OnOutput(byte[] data) => Output.Append(DockerStreamType.Stdout, data);

    private void OnError(byte[] data) => Output.Append(DockerStreamType.Stderr, data);

    private void OnExited(int exitCode)
    {
        _exitCodeValue = exitCode;
        _hasExited = true;
        FinishedAt ??= DateTimeOffset.UtcNow;
        _exitCode.TrySetResult(exitCode);
        Output.Complete();
    }
}
