using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WslcDockerSocket.Api;
using WslcDockerSocket.Api.Contracts;
using WslcDockerSocket.Hosting;
using WslcDockerSocket.Streaming;

namespace WslcDockerSocket.Engine;

internal sealed class WslcDockerEngine : IDisposable
{
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly JsonElement EmptyJsonObject = JsonDocument.Parse("{}").RootElement.Clone();
    private readonly IWslcCommandRunner _commandRunner;
    private readonly WslcCliContainerCatalog _containerCatalog;
    private readonly WslcCliImageCatalog _imageCatalog;
    private readonly WslcCliResourceCatalog _resourceCatalog;
    private readonly WslRuntimeDiagnosticsProvider _runtimeDiagnostics;
    private readonly Uri? _dockerSocketMountDockerHost;
    // Exec records are transient request metadata only; container existence and lifecycle always come from WSLC.
    private readonly ConcurrentDictionary<string, DockerExecState> _execs = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public WslcDockerEngine(DockerSocketMountAdvertisement? mountAdvertisement = null)
        : this(new WslcCommandRunner(), new WslRuntimeDiagnosticsProvider(), mountAdvertisement)
    {
    }

    internal WslcDockerEngine(IWslcCommandRunner commandRunner, WslRuntimeDiagnosticsProvider runtimeDiagnostics,
        DockerSocketMountAdvertisement? mountAdvertisement = null)
    {
        _commandRunner = commandRunner;
        _containerCatalog = new WslcCliContainerCatalog(commandRunner);
        _imageCatalog = new WslcCliImageCatalog(commandRunner);
        _resourceCatalog = new WslcCliResourceCatalog(commandRunner);
        _runtimeDiagnostics = runtimeDiagnostics;
        _dockerSocketMountDockerHost = (mountAdvertisement ?? DockerSocketMountAdvertisement.None).DockerHost;
    }

    public static object GetVersion() => new
    {
        Version = typeof(WslcDockerEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        ApiVersion = "1.43",
        MinAPIVersion = "1.24",
        Os = "linux",
        Arch = DockerArchitecture.ToVersionArchitecture(RuntimeInformation.ProcessArchitecture),
        Experimental = false,
    };

    public async Task<object> GetInfoAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var diagnosticsTask = _runtimeDiagnostics.GetAsync();
        var containersTask = _containerCatalog.ListAsync(ct);
        var imagesTask = _imageCatalog.CountAsync(ct);
        await Task.WhenAll(diagnosticsTask, containersTask, imagesTask).ConfigureAwait(false);
        var containers = await containersTask.ConfigureAwait(false);
        var diagnostics = await diagnosticsTask.ConfigureAwait(false);
        return new
        {
            ID = Environment.MachineName,
            Containers = containers.Count,
            ContainersRunning = containers.Count(container => container.DockerState == "running"),
            ContainersPaused = 0,
            ContainersStopped = containers.Count(container => container.DockerState == "exited"),
            Images = await imagesTask.ConfigureAwait(false),
            Driver = "wslc",
            OSType = "linux",
            Architecture = DockerArchitecture.ToInfoArchitecture(RuntimeInformation.ProcessArchitecture),
            NCPU = Environment.ProcessorCount,
            diagnostics.MemTotal,
            diagnostics.KernelVersion,
            diagnostics.OperatingSystem,
            Name = Environment.MachineName,
            diagnostics.ServerVersion,
        };
    }

    public async Task<IReadOnlyList<JsonElement>> ListImagesAsync(IQueryCollection query, CancellationToken ct)
    {
        var filters = DockerFilters.FromQuery(query, "label", "reference", "dangling");
        var images = await _imageCatalog.ListAsync(ct).ConfigureAwait(false);
        return filters.IsEmpty ? images : [.. images.Where(image => MatchesImage(image, filters))];
    }

    private static bool MatchesImage(JsonElement image, DockerFilters filters)
    {
        var tags = image.TryGetProperty("RepoTags", out var repoTags) && repoTags.ValueKind == JsonValueKind.Array
            ? repoTags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String)
                .Select(tag => tag.GetString() ?? string.Empty).ToArray()
            : [];
        // Docker treats an image with no repository tag as dangling.
        if (!filters.MatchesBoolean("dangling", tags.Length == 0)) return false;
        if (!filters.MatchesLabels(image.TryGetProperty("Labels", out var labels) ? labels : default)) return false;
        var references = filters.Values("reference");
        return references.Count == 0
               || references.Any(reference => tags.Any(tag => DockerImageReference.Parse(reference).Matches(tag)));
    }

    public Task<JsonElement> InspectImageAsync(string image, CancellationToken ct) => _imageCatalog.InspectAsync(image, ct);

    public Task PullImageAsync(DockerImageReference image, string registryAuth, CancellationToken ct)
    {
        ThrowIfDisposed();
        switch (ClassifyRegistryAuth(registryAuth))
        {
            case RegistryAuthKind.Malformed:
                throw new DockerApiException(StatusCodes.Status400BadRequest, "X-Registry-Auth is invalid.");
            case RegistryAuthKind.Credentialed:
                throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    "Registry authentication is not supported by the WSLC Docker socket.");
        }

        return _imageCatalog.PullAsync(image.CanonicalName, ct);
    }

    public async Task<string> CreateContainerAsync(string requestedName, DockerCreateContainerRequest request, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Image))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Image is required");
        }

        RejectUnsupportedConfiguration(request, _dockerSocketMountDockerHost);
        var image = DockerImageReference.Parse(request.Image);
        var command = BuildCreateCommand(requestedName, request, image.CanonicalName, _dockerSocketMountDockerHost);
        var result = await _commandRunner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(command, result, requestedName);
        }

        var identifier = ReadCreatedContainerId(result.StandardOutput);
        var reconciliationKey = !string.IsNullOrWhiteSpace(identifier) ? identifier : requestedName;
        if (string.IsNullOrWhiteSpace(reconciliationKey))
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                "WSLC created a container but did not return an identifier.");
        }

        return (await _containerCatalog.ResolveAsync(reconciliationKey, ct).ConfigureAwait(false)).Id;
    }

    public async Task<bool> StartContainerAsync(string id, CancellationToken ct)
    {
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        if (container.DockerState == "running")
        {
            return false;
        }

        await RunMutationAsync(["container", "start", container.Id], ct).ConfigureAwait(false);
        await _containerCatalog.ResolveAsync(container.Id, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StopContainerAsync(string id, CancellationToken ct)
    {
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        if (container.DockerState != "running")
        {
            return false;
        }

        await RunMutationAsync(["container", "stop", container.Id], ct).ConfigureAwait(false);
        return true;
    }

    public async Task CopyArchiveToContainerAsync(string id, string path, Stream archive, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(archive);
        ValidateArchiveDestinationPath(path);
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        var command = new[] { "container", "cp", "-", $"{container.Id}:{path}" };
        var result = await _commandRunner.RunWithStandardInputAsync(command, archive, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(command, result, null);
        }
    }

    public async Task WaitForContainerAsync(string id, HttpResponse response, CancellationToken ct)
    {
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        while (true)
        {
            var inspect = await _containerCatalog.InspectAsync(container.Id, ct).ConfigureAwait(false);
            if (!IsContainerRunning(inspect))
            {
                if (!TryGetExitCode(inspect, out var exitCode))
                {
                    throw new DockerApiException(StatusCodes.Status501NotImplemented,
                        "WSLC inspect does not provide an exit code required by Docker wait.");
                }

                await JsonSerializer.SerializeAsync(response.Body,
                    new { StatusCode = exitCode, Error = new { Message = string.Empty } }, DockerJson.Options, ct).ConfigureAwait(false);
                return;
            }

            await Task.Delay(WaitPollInterval, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteContainerAsync(string id, bool force, CancellationToken ct)
    {
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        var command = new List<string> { "container", "remove" };
        if (force)
        {
            command.Add("--force");
        }

        command.Add(container.Id);
        await RunMutationAsync(command, ct).ConfigureAwait(false);
        _execs.Where(entry => entry.Value.ContainerId.Equals(container.Id, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key).ToList().ForEach(execId => _execs.TryRemove(execId, out _));
    }

    public Task<JsonElement> InspectContainerAsync(string id, CancellationToken ct) => _containerCatalog.InspectAsync(id, ct);

    public async Task<object> ListVolumesAsync(IQueryCollection query, CancellationToken ct) => new
    {
        Volumes = Filter(await _resourceCatalog.ListAndInspectAsync("volume", ct).ConfigureAwait(false),
            DockerFilters.FromQuery(query, "label", "name", "driver")),
        Warnings = Array.Empty<string>(),
    };

    private static IReadOnlyList<JsonElement> Filter(IReadOnlyList<JsonElement> resources, DockerFilters filters)
    {
        if (filters.IsEmpty) return resources;
        return
        [
            .. resources.Where(resource =>
                filters.MatchesLabels(resource.TryGetProperty("Labels", out var labels) ? labels : default)
                && filters.MatchesSubstring("name", ReadResourceString(resource, "Name"))
                && filters.MatchesExact("driver", ReadResourceString(resource, "Driver"))
                && filters.MatchesIdPrefix("id", ReadResourceString(resource, "Id"))),
        ];
    }

    private static string? ReadResourceString(JsonElement resource, string name) =>
        resource.ValueKind == JsonValueKind.Object && resource.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public Task<JsonElement> InspectVolumeAsync(string name, CancellationToken ct) => _resourceCatalog.InspectAsync("volume", name, ct);

    public Task DeleteVolumeAsync(string name, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _resourceCatalog.RemoveAsync("volume", name, ct);
    }

    public Task DeleteNetworkAsync(string idOrName, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _resourceCatalog.RemoveAsync("network", idOrName, ct);
    }

    public Task<IReadOnlyList<object>> DeleteImageAsync(string image, bool force, bool noPrune, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _imageCatalog.RemoveAsync(image, force, noPrune, ct);
    }
    public async Task<IReadOnlyList<JsonElement>> ListNetworksAsync(IQueryCollection query, CancellationToken ct) =>
        Filter(await _resourceCatalog.ListAndInspectAsync("network", ct).ConfigureAwait(false),
            DockerFilters.FromQuery(query, "label", "name", "driver", "id"));
    public Task<JsonElement> InspectNetworkAsync(string idOrName, CancellationToken ct) => _resourceCatalog.InspectAsync("network", idOrName, ct);

    public async Task<IReadOnlyList<object>> ListContainersAsync(IQueryCollection query, CancellationToken ct)
    {
        var includeStopped = string.Equals(query["all"], "1", StringComparison.Ordinal)
                             || string.Equals(query["all"], "true", StringComparison.OrdinalIgnoreCase);
        var filters = DockerFilters.FromQuery(query, "label", "id", "name", "status");
        var containers = await _containerCatalog.ListAsync(ct).ConfigureAwait(false);
        var visible = containers
            .Where(container => includeStopped || container.DockerState == "running")
            .Where(container => filters.MatchesIdPrefix("id", container.Id)
                                && filters.MatchesSubstring("name", container.Name)
                                && filters.MatchesExact("status", container.DockerState))
            .ToList();
        var inspected = await _containerCatalog.InspectManyAsync([.. visible.Select(container => container.Id)], ct)
            .ConfigureAwait(false);
        // Labels are only available from inspect, so label filters are applied after the details are read.
        return
        [
            .. visible.Select(container => (Container: container, Detail: Detail(container, inspected)))
                .Where(entry => filters.MatchesLabels(ToListLabels(entry.Detail)))
                .Select(entry => ToListResponse(entry.Container, inspected)),
        ];
    }

    public async Task<DockerExecState> CreateExecAsync(string containerId, DockerExecCreateRequest request, CancellationToken ct)
    {
        if (request.Cmd is not { Length: > 0 })
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Exec command is required");
        }

        if (request.Env is { Length: > 0 } || !string.IsNullOrWhiteSpace(request.WorkingDir))
        {
            throw new DockerApiException(StatusCodes.Status501NotImplemented,
                "Exec environment variables and working directories are not supported by the WSLC Docker socket yet.");
        }

        var container = await _containerCatalog.ResolveAsync(containerId, ct).ConfigureAwait(false);
        if (container.DockerState != "running")
        {
            throw new DockerApiException(StatusCodes.Status409Conflict, $"Container {container.Id} is not running");
        }

        var exec = new DockerExecState(Guid.NewGuid().ToString("N"), container.Id, request.Cmd);
        if (!_execs.TryAdd(exec.Id, exec))
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError, "Failed to record the exec instance.");
        }

        return exec;
    }

    public void EnsureExecExists(string id)
    {
        if (!_execs.ContainsKey(id))
        {
            throw new DockerApiException(StatusCodes.Status404NotFound, $"No such exec instance: {id}");
        }
    }

    public async Task<IReadOnlyList<DockerOutputFrame>> StartExecAsync(string id, CancellationToken ct)
    {
        if (!_execs.TryGetValue(id, out var exec))
        {
            throw new DockerApiException(StatusCodes.Status404NotFound, $"No such exec instance: {id}");
        }

        var result = await exec.StartAsync(_commandRunner, ct).ConfigureAwait(false);
        return DockerStreams.FromStdoutAndStderr(result.StandardOutputBytes, result.StandardErrorBytes);
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
            ProcessConfig = new { Entrypoint = exec.Command[0], Arguments = exec.Command.Skip(1) },
        };
    }

    public async Task StreamLogsAsync(string id, Func<DockerOutputFrame, CancellationToken, ValueTask> writeFrameAsync, CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(writeFrameAsync);
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        await _commandRunner.StreamAsync(["container", "logs", "--follow", container.Id], writeFrameAsync, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DockerOutputFrame>> GetLogsAsync(string id, bool follow, CancellationToken ct)
    {
        var container = await _containerCatalog.ResolveAsync(id, ct).ConfigureAwait(false);
        var command = new List<string> { "container", "logs" };
        if (follow)
        {
            command.Add("--follow");
        }

        command.Add(container.Id);
        var result = await _commandRunner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(command, result, null);
        }

        return DockerStreams.FromStdoutAndStderr(Encoding.UTF8.GetBytes(result.StandardOutput),
            Encoding.UTF8.GetBytes(result.StandardError));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _execs.Clear();
        }
    }

    private static List<string> BuildCreateCommand(string requestedName, DockerCreateContainerRequest request, string image, Uri? dockerSocketMountDockerHost)
    {
        var command = new List<string> { "container", "create" };
        if (request.HostConfig?.AutoRemove == true) command.Add("--rm");
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            command.AddRange(["--name", requestedName]);
        }

        if (!string.IsNullOrWhiteSpace(request.Hostname)) command.AddRange(["--hostname", request.Hostname]);
        foreach (var environment in request.Env ?? [])
        {
            DockerEnvironment.Parse([environment]);
            command.AddRange(["--env", environment]);
        }

        // A sole Docker-socket bind mount cannot be backed by a real file; the container gets network access to
        // this engine over TCP instead, which is what such containers (e.g. Testcontainers' Ryuk) actually want.
        if (dockerSocketMountDockerHost is not null && DockerSocketMount.IsSoleDockerSocketBind(request.HostConfig))
        {
            command.AddRange(["--env", $"DOCKER_HOST={dockerSocketMountDockerHost}"]);
        }

        if (request.Entrypoint is { Length: > 0 }) command.AddRange(["--entrypoint", request.Entrypoint[0]]);
        foreach (var (key, value) in request.Labels ?? []) command.AddRange(["--label", $"{key}={value}"]);
        foreach (var binding in DockerPortBinding.Parse(request.HostConfig?.PortBindings)) command.AddRange(["--publish", binding.ToWslcPublishArgument()]);
        command.Add(image);
        if (request.Entrypoint is { Length: > 1 }) command.AddRange(request.Entrypoint[1..]);
        command.AddRange(request.Cmd ?? []);
        return command;
    }

    private async Task RunMutationAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        ThrowIfDisposed();
        var result = await _commandRunner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(command, result, null);
        }
    }

    private static void ThrowMutationFailure(IReadOnlyList<string> command, WslcCommandResult result, string? requestedName)
    {
        var detail = (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
        if (!string.IsNullOrWhiteSpace(requestedName)
            && (detail.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("already in use", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DockerApiException(StatusCodes.Status409Conflict,
                $"Conflict. The container name \"/{requestedName}\" is already in use.");
        }

        throw new DockerApiException(StatusCodes.Status500InternalServerError,
            $"wslc {string.Join(' ', command)} failed: {detail}");
    }

    private static string? ReadCreatedContainerId(string output)
    {
        var trimmed = output.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
    }

    private static bool IsContainerRunning(JsonElement inspect)
    {
        if (inspect.TryGetProperty("State", out var state))
        {
            if (state.ValueKind == JsonValueKind.String) return state.GetString()?.Equals("running", StringComparison.OrdinalIgnoreCase) == true;
            if (state.ValueKind == JsonValueKind.Object)
            {
                if (state.TryGetProperty("Running", out var running) && running.ValueKind is JsonValueKind.True or JsonValueKind.False) return running.GetBoolean();
                if (state.TryGetProperty("Status", out var status) && status.ValueKind == JsonValueKind.String) return status.GetString()?.Equals("running", StringComparison.OrdinalIgnoreCase) == true;
            }
        }

        return false;
    }

    private static bool TryGetExitCode(JsonElement inspect, out int exitCode)
    {
        exitCode = default;
        if (!inspect.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("ExitCode", out var value)) return false;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out exitCode);
    }

    private static JsonElement Detail(WslcCatalogContainer container,
        IReadOnlyDictionary<string, JsonElement> inspected) =>
        inspected.TryGetValue(container.Id, out var detail) ? detail : default;

    private static object ToListResponse(WslcCatalogContainer container,
        IReadOnlyDictionary<string, JsonElement> inspected)
    {
        var detail = Detail(container, inspected);
        return new
        {
            container.Id,
            Names = new[] { "/" + container.Name.TrimStart('/') },
            // Docker reports the reference the container was created from, and the image digest separately.
            Image = ReadConfigString(detail, "Image") ?? container.Image,
            ImageID = ReadString(detail, "Image") ?? container.Image,
            Command = ToListCommand(detail),
            Created = container.CreatedAt,
            State = container.DockerState,
            // WSLC's list reports Docker's own status text; older releases omit it.
            Status = string.IsNullOrWhiteSpace(container.Status)
                ? container.DockerState switch { "running" => "Up", "created" => "Created", _ => "Exited" }
                : container.Status,
            Labels = ToListLabels(detail),
            Ports = ToListPorts(detail),
            NetworkSettings = new { Networks = ToListNetworks(detail) },
        };
    }

    /// <summary>
    /// Reads labels from inspect rather than the listing. WSLC renders list labels as one comma-joined string,
    /// which cannot be split safely because label values may themselves contain commas.
    /// </summary>
    private static JsonElement ToListLabels(JsonElement detail) =>
        detail.ValueKind == JsonValueKind.Object
        && detail.TryGetProperty("Labels", out var labels)
        && labels.ValueKind == JsonValueKind.Object
            ? labels
            : EmptyJsonObject;

    /// <summary>Rebuilds the container command, which the listing reports only as truncated display text.</summary>
    private static string ToListCommand(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty("Config", out var config)
            || config.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var name in (string[])["Entrypoint", "Cmd"])
        {
            if (!config.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) continue;
            parts.AddRange(value.EnumerateArray()
                .Where(argument => argument.ValueKind == JsonValueKind.String)
                .Select(argument => argument.GetString() ?? string.Empty));
        }

        return string.Join(' ', parts);
    }

    /// <summary>Projects the inspected port bindings onto Docker's flat list-response port shape.</summary>
    private static object[] ToListPorts(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty("Ports", out var ports)
            || ports.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var listed = new List<object>();
        foreach (var port in ports.EnumerateObject())
        {
            var separator = port.Name.IndexOf('/');
            if (!int.TryParse(separator < 0 ? port.Name : port.Name[..separator], out var privatePort)) continue;
            var type = separator < 0 ? "tcp" : port.Name[(separator + 1)..];
            if (port.Value.ValueKind != JsonValueKind.Array || port.Value.GetArrayLength() == 0)
            {
                // Docker reports an exposed but unpublished port without a host address or public port.
                listed.Add(new { PrivatePort = privatePort, Type = type });
                continue;
            }

            foreach (var binding in port.Value.EnumerateArray())
            {
                listed.Add(new
                {
                    IP = ReadString(binding, "HostIp"),
                    PrivatePort = privatePort,
                    PublicPort = int.TryParse(ReadString(binding, "HostPort"), out var publicPort) ? publicPort : 0,
                    Type = type,
                });
            }
        }

        return [.. listed];
    }

    private static JsonElement ToListNetworks(JsonElement detail) =>
        detail.ValueKind == JsonValueKind.Object
        && detail.TryGetProperty("NetworkSettings", out var networkSettings)
        && networkSettings.ValueKind == JsonValueKind.Object
        && networkSettings.TryGetProperty("Networks", out var networks)
        && networks.ValueKind == JsonValueKind.Object
            ? networks
            : EmptyJsonObject;

    private static string? ReadConfigString(JsonElement detail, string name) =>
        detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("Config", out var config)
            ? ReadString(config, name) is { Length: > 0 } value ? value : null
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool HasExplicitPortBinding(string exposedPort, Dictionary<string, List<DockerHostPortBinding>?>? bindings)
    {
        var separator = exposedPort.IndexOf('/');
        var exposedNumber = separator < 0 ? exposedPort : exposedPort[..separator];
        return bindings?.Keys.Any(binding =>
        {
            var bindingSeparator = binding.IndexOf('/');
            return (bindingSeparator < 0 ? binding : binding[..bindingSeparator])
                .Equals(exposedNumber, StringComparison.Ordinal);
        }) == true;
    }

    private static RegistryAuthKind ClassifyRegistryAuth(string registryAuth)
    {
        if (string.IsNullOrWhiteSpace(registryAuth) || registryAuth.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return RegistryAuthKind.Anonymous;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(registryAuth));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RegistryAuthKind.Malformed;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (IsCredentialProperty(property.Name) && HasCredentialValue(property.Value))
                {
                    return RegistryAuthKind.Credentialed;
                }
            }

            return RegistryAuthKind.Anonymous;
        }
        catch (FormatException)
        {
            return RegistryAuthKind.Malformed;
        }
        catch (JsonException)
        {
            return RegistryAuthKind.Malformed;
        }
    }

    private static bool IsCredentialProperty(string name) => name.Equals("username", StringComparison.OrdinalIgnoreCase)
        || name.Equals("password", StringComparison.OrdinalIgnoreCase)
        || name.Equals("identitytoken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("registrytoken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("auth", StringComparison.OrdinalIgnoreCase);

    private static bool HasCredentialValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => true,
    };

    private enum RegistryAuthKind
    {
        Anonymous,
        Credentialed,
        Malformed,
    }

    private static void ValidateArchiveDestinationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Archive destination path is required.");
        }

        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest,
                "Archive destination path must be an absolute Unix path without control characters.");
        }
    }

    private static void RejectUnsupportedConfiguration(DockerCreateContainerRequest request, Uri? dockerSocketMountDockerHost)
    {
        if (request.Tty) throw new DockerApiException(StatusCodes.Status501NotImplemented, "TTY containers are not supported by the WSLC Docker socket yet.");
        if (request.HostConfig?.Privileged == true) throw new DockerApiException(StatusCodes.Status501NotImplemented, "Privileged containers are not supported by the WSLC Docker socket yet.");
        if (!string.IsNullOrWhiteSpace(request.WorkingDir)) throw new DockerApiException(StatusCodes.Status501NotImplemented, "WorkingDir is not supported by the WSLC Docker socket yet.");
        if (request.ExposedPorts is { Count: > 0 } && request.ExposedPorts.Keys.Any(port => !HasExplicitPortBinding(port, request.HostConfig?.PortBindings))) throw new DockerApiException(StatusCodes.Status501NotImplemented, "ExposedPorts without a matching explicit port binding are not supported by the WSLC Docker socket yet.");
        if (!string.IsNullOrWhiteSpace(request.HostConfig?.NetworkMode) && !request.HostConfig.NetworkMode.Equals("default", StringComparison.OrdinalIgnoreCase) && !request.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase)) throw new DockerApiException(StatusCodes.Status501NotImplemented, "Only the default bridge network mode is supported by the WSLC Docker socket.");
        if (request.HostConfig?.Binds is { Length: > 0 } || request.HostConfig?.Mounts is { Count: > 0 } || request.HostConfig?.Tmpfs is { Count: > 0 })
        {
            var isSoleDockerSocketBind = DockerSocketMount.IsSoleDockerSocketBind(request.HostConfig);
            if (!isSoleDockerSocketBind) throw new DockerApiException(StatusCodes.Status501NotImplemented, "Bind mounts, volumes, tmpfs mounts, and Docker socket mounts are not supported by the WSLC Docker socket yet.");
            if (dockerSocketMountDockerHost is null) throw new DockerApiException(StatusCodes.Status501NotImplemented, "Docker socket mounts are only supported when WSLC_DOCKER_SOCKET_DISABLE_HYPERV_TCP is not set to true, so the container can reach this engine over TCP instead of a mounted socket.");
        }

        if (request.NetworkingConfig?.EndpointsConfig is { Count: > 0 }) throw new DockerApiException(StatusCodes.Status501NotImplemented, "Custom Docker networks are not supported by the WSLC Docker socket yet.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
