namespace WslcDockerSocket.Engine;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Api;
using Api.Contracts;
using Microsoft.WSL.Containers;

internal sealed class WslcDockerEngine : IDisposable
{
    private const string SessionApplicationName = "WslcDockerSocket";
    private readonly ConcurrentDictionary<string, DockerContainerState> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DockerExecState> _execs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sessionLock = new();
    private Session? _session;
    private int _disposed;

    public static object GetVersion()
    {
        return new
        {
            // Docker API versions describe this adapter's protocol surface, not a WSLC daemon version.
            Version = GetWslcSdkVersion(),
            ApiVersion = "1.43",
            MinAPIVersion = "1.24",
            Os = "linux",
            Arch = DockerArchitecture.ToVersionArchitecture(RuntimeInformation.ProcessArchitecture),
            Experimental = false,
        };
    }

    public object GetInfo()
    {
        var containers = _containers.Values.ToArray();
        return new
        {
            // Container and image counts are scoped to this adapter's active WSLC session.
            ID = SessionApplicationName,
            Containers = containers.Length,
            ContainersRunning = containers.Count(container => container.State == "running"),
            ContainersPaused = 0,
            ContainersStopped = containers.Count(container => container.State == "exited"),
            Images = GetSession().GetImages().Count,
            Driver = "wslc",
            OSType = "linux",
            Architecture = DockerArchitecture.ToInfoArchitecture(RuntimeInformation.ProcessArchitecture),
            NCPU = Environment.ProcessorCount,
            Name = Environment.MachineName,
            ServerVersion = GetWslcSdkVersion(),
        };
    }

    public object InspectImage(string image)
    {
        var reference = DockerImageReference.Parse(image);
        var existing = GetSession().GetImages().FirstOrDefault(candidate => reference.Matches(candidate.Name)) ?? throw NoSuchImage(image);

        return new
        {
            // WSLC exposes image names through the managed projection, but not Docker image IDs/config.
            // Keep the adapter identifier stable without presenting it as a WSLC SHA256 digest.
            Id = "wslc:" + DockerId.From(existing.Name),
            RepoTags = new[] { existing.Name },
            RepoDigests = Array.Empty<string>(),
        };
    }

    public async Task PullImageAsync(DockerImageReference image, string registryAuth, CancellationToken ct)
    {
        var options = new PullImageOptions(image.CanonicalName)
        {
            RegistryAuth = DecodeRegistryAuth(registryAuth),
        };
        var operation = GetSession().PullImageAsync(options);
        using var registration = ct.Register(operation.Cancel);
        try
        {
            await operation;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public DockerContainerState CreateContainer(string requestedName, DockerCreateContainerRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Image))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Image is required");
        }

        RejectUnsupportedConfiguration(request);
        var image = DockerImageReference.Parse(request.Image);
        var command = DockerCommand.Combine(request.Entrypoint, request.Cmd);
        var initSettings = new ProcessSettings
        {
            OutputMode = ProcessOutputMode.Event,
            EnvironmentVariables = DockerEnvironment.Parse(request.Env),
        };
        if (command.Count > 0)
        {
            initSettings.CommandLine = [.. command];
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDir))
        {
            initSettings.WorkingDirectory = request.WorkingDir;
        }

        var portBindings = DockerPortBinding.Parse(request.HostConfig?.PortBindings);
        var settings = new ContainerSettings(image.CanonicalName)
        {
            Name = string.IsNullOrWhiteSpace(requestedName) ? null : requestedName,
            HostName = request.Hostname,
            EnableGpu = false,
            Privileged = request.HostConfig?.Privileged ?? false,
            NetworkingMode = ContainerNetworkingMode.Bridged,
            EnableAutoRemove = false,
            InitProcess = initSettings,
            PortMappings = [.. portBindings.Select(binding => binding.ToWslc())],
        };

        var runtime = GetSession().CreateContainer(settings);
        var id = runtime.Id;
        var name = string.IsNullOrWhiteSpace(requestedName) ? id[..Math.Min(12, id.Length)] : requestedName;
        if (_containers.Values.Any(container => string.Equals(container.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            runtime.Delete(DeleteContainerOption.Force);
            runtime.Dispose();
            throw new DockerApiException(StatusCodes.Status409Conflict,
                $"Conflict. The container name \"/{name}\" is already in use.");
        }

        var container = new DockerContainerState(id, name, image, request, runtime, portBindings);
        if (!_containers.TryAdd(id, container))
        {
            container.Dispose();
            throw new DockerApiException(StatusCodes.Status500InternalServerError, "Failed to record the WSLC container.");
        }

        return container;
    }

    public bool StartContainer(string id)
    {
        var container = GetContainer(id);
        if (container.State == "running")
        {
            return false;
        }

        container.Start();
        return true;
    }

    public bool StopContainer(string id)
    {
        var container = GetContainer(id);
        if (container.State != "running")
        {
            return false;
        }

        container.Stop();
        return true;
    }

    public Task<int> WaitForContainerAsync(string id, CancellationToken ct) => GetContainer(id).WaitForExitAsync(ct);

    public void DeleteContainer(string id, bool force)
    {
        var container = GetContainer(id);
        container.Delete(force);
        _containers.TryRemove(container.Id, out _);
        RemoveExecsForContainer(container.Id);
        container.Dispose();
    }

    public DockerContainerState GetContainer(string idOrName)
    {
        var container = _containers.Values.FirstOrDefault(candidate => candidate.Matches(idOrName));
        return container ?? throw new DockerApiException(StatusCodes.Status404NotFound, $"No such container: {idOrName}");
    }

    public object InspectContainer(string id) => GetContainer(id).ToInspectResponse();

    public IEnumerable<object> ListContainers(IQueryCollection query)
    {
        var includeStopped = string.Equals(query["all"], "1", StringComparison.Ordinal)
                             || string.Equals(query["all"], "true", StringComparison.OrdinalIgnoreCase);
        return [.. _containers.Values
            .Where(container => includeStopped || container.State == "running")
            .Select(container => container.ToListResponse())];
    }

    public DockerExecState CreateExec(string containerId, DockerExecCreateRequest request)
    {
        var container = GetContainer(containerId);
        if (container.State != "running")
        {
            throw new DockerApiException(StatusCodes.Status409Conflict, $"Container {container.Id} is not running");
        }

        if (request.Cmd is not { Length: > 0 })
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Exec command is required");
        }

        var exec = new DockerExecState(Guid.NewGuid().ToString("N"), container, request);
        if (!_execs.TryAdd(exec.Id, exec))
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError, "Failed to record the exec instance.");
        }

        return exec;
    }

    public Task<IReadOnlyList<Streaming.DockerOutputFrame>> StartExecAsync(string id, CancellationToken ct)
    {
        if (!_execs.TryGetValue(id, out var exec))
        {
            throw new DockerApiException(StatusCodes.Status404NotFound, $"No such exec instance: {id}");
        }

        return exec.StartAsync(ct);
    }

    public object InspectExec(string id)
    {
        if (!_execs.TryGetValue(id, out var exec))
        {
            throw new DockerApiException(StatusCodes.Status404NotFound, $"No such exec instance: {id}");
        }

        return new
        {
            ID = exec.Id,
            exec.Running,
            exec.ExitCode,
            ProcessConfig = new
            {
                Entrypoint = exec.Command.Count > 0 ? exec.Command[0] : string.Empty,
                Arguments = exec.Command.Count > 1 ? exec.Command.Skip(1) : []
            },
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _execs.Clear();
        foreach (var container in _containers.Values)
        {
            container.Dispose();
        }

        _containers.Clear();
        lock (_sessionLock)
        {
            _session?.Terminate();
            _session?.Dispose();
            _session = null;
        }
    }

    private Session GetSession()
    {
        ThrowIfDisposed();
        lock (_sessionLock)
        {
            if (_session != null)
            {
                return _session;
            }

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WslcDockerSocket", "session");
            Directory.CreateDirectory(root);
            var session = new Session(new SessionSettings(SessionApplicationName, root));
            session.Start();
            _session = session;
            return session;
        }
    }

    private static string GetWslcSdkVersion()
    {
        var version = typeof(Session).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }

    private static DockerApiException NoSuchImage(string image) => new(StatusCodes.Status404NotFound,
        $"No such image: {image}");

    private static string? DecodeRegistryAuth(string registryAuth)
    {
        if (string.IsNullOrWhiteSpace(registryAuth) || registryAuth == "null")
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(registryAuth));
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("identitytoken", out var identityToken)
                || document.RootElement.TryGetProperty("IdentityToken", out identityToken))
            {
                return identityToken.GetString();
            }

            if (document.RootElement.TryGetProperty("username", out _)
                || document.RootElement.TryGetProperty("Username", out _))
            {
                throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    "Username/password registry authentication is not supported by WSLC; use an identity token.");
            }
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static void RejectUnsupportedConfiguration(DockerCreateContainerRequest request)
    {
        if (request.Tty)
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "TTY containers are not supported by the WSLC Docker socket yet.");
        }

        if (request.HostConfig?.AutoRemove == true)
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "HostConfig.AutoRemove is not supported by the WSLC Docker socket yet.");
        }

        if (!string.IsNullOrWhiteSpace(request.HostConfig?.NetworkMode)
            && !request.HostConfig.NetworkMode.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !request.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "Only the default bridge network mode is supported by the WSLC Docker socket.");
        }

        if (request.HostConfig?.Binds is { Length: > 0 }
            || request.HostConfig?.Mounts is { Count: > 0 }
            || request.HostConfig?.Tmpfs is { Count: > 0 })
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "Bind mounts, volumes, tmpfs mounts, and Docker socket mounts are not supported by the WSLC Docker socket yet.");
        }

        if (request.NetworkingConfig?.EndpointsConfig is { Count: > 0 })
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "Custom Docker networks are not supported by the WSLC Docker socket yet.");
        }
    }

    private void RemoveExecsForContainer(string containerId)
    {
        foreach (var (id, exec) in _execs.Where(entry => entry.Value.ContainerId.Equals(containerId, StringComparison.OrdinalIgnoreCase)))
        {
            _execs.TryRemove(id, out _);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(WslcDockerEngine));
}
