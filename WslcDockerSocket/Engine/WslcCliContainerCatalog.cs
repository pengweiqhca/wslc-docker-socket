namespace WslcDockerSocket.Engine;

using System.Text.Json;
using System.Text.Json.Nodes;
using Api;

/// <summary>
/// Queries containers in the unqualified WSLC CLI scope.
/// </summary>
internal sealed class WslcCliContainerCatalog(IWslcCommandRunner runner)
{
    private const int MaximumInspectBatchSize = 100;

    public async Task<IReadOnlyList<WslcCatalogContainer>> ListAsync(CancellationToken ct)
    {
        var result = await RunAsync(["container", "list", "-a", "--format", "json"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        var containers = new List<WslcCatalogContainer>();
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            foreach (var line in lines) AddListItem(containers, ParseObject(line));
        }
        else
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var element in document.RootElement.EnumerateArray()) AddListItem(containers, element);
                    break;
                case JsonValueKind.Object:
                    AddListItem(containers, document.RootElement);
                    break;
                default:
                    throw new DockerApiException(StatusCodes.Status500InternalServerError,
                        "WSLC returned an invalid container list response.");
            }
        }

        return containers;
    }

    /// <summary>
    /// Reads one list entry. WSLC 2.9.10 aligned this output with Docker's CLI: <c>Id</c> became <c>ID</c>,
    /// <c>Name</c> became <c>Names</c>, and <c>State</c>/<c>CreatedAt</c> became text. Both shapes are accepted so
    /// the adapter keeps working across WSLC versions.
    /// </summary>
    private static void AddListItem(List<WslcCatalogContainer> containers, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var id = ReadString(element, "Id") ?? ReadString(element, "ID");
        if (string.IsNullOrWhiteSpace(id)) return;

        containers.Add(new WslcCatalogContainer(
            id,
            ReadPrimaryName(element),
            ReadString(element, "Image") ?? string.Empty,
            ReadDockerState(element),
            ReadCreatedSeconds(element),
            ReadString(element, "Status") ?? string.Empty));
    }

    private static string ReadPrimaryName(JsonElement element)
    {
        var name = ReadString(element, "Name") ?? ReadString(element, "Names");
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        // Docker's list reports every name for a container; the first is its canonical name.
        var separator = name.IndexOf(',');
        return (separator < 0 ? name : name[..separator]).Trim().TrimStart('/');
    }

    private static string ReadDockerState(JsonElement element)
    {
        if (!TryGetProperty(element, "State", out var state)) return "exited";
        if (state.ValueKind == JsonValueKind.String)
        {
            var value = state.GetString();
            return string.IsNullOrWhiteSpace(value) ? "exited" : value.Trim().ToLowerInvariant();
        }

        // Older WSLC releases reported the native numeric lifecycle state.
        return state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var numeric)
            ? numeric switch { 1 => "created", 2 => "running", 3 => "exited", _ => "exited" }
            : "exited";
    }

    private static long ReadCreatedSeconds(JsonElement element)
    {
        if (!TryGetProperty(element, "CreatedAt", out var createdAt)) return 0;
        if (createdAt.ValueKind == JsonValueKind.Number && createdAt.TryGetInt64(out var seconds)) return seconds;
        return createdAt.ValueKind == JsonValueKind.String
               && WslcNativeFormat.TryParseUnixSeconds(createdAt.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public async Task<WslcCatalogContainer> ResolveAsync(string idOrName, CancellationToken ct)
    {
        var matches = (await ListAsync(ct).ConfigureAwait(false))
            .Where(container => MatchesId(container.Id, idOrName)
                || string.Equals(container.Name, idOrName.TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            0 => throw NoSuchContainer(idOrName),
            1 => matches[0],
            _ => throw new DockerApiException(StatusCodes.Status409Conflict,
                $"Container identifier '{idOrName}' is ambiguous."),
        };
    }

    /// <summary>
    /// Inspects many containers in one CLI invocation, keyed by container id. WSLC's list output omits the
    /// network address, so Docker's list response can only report it from inspect.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> InspectManyAsync(IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        var inspected = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < ids.Count; offset += MaximumInspectBatchSize)
        {
            var command = new List<string> { "container", "inspect" };
            command.AddRange(ids.Skip(offset).Take(MaximumInspectBatchSize));
            command.AddRange(["--format", "json"]);
            var batch = ids.Skip(offset).Take(MaximumInspectBatchSize).ToList();
            var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            // Containers removed since the listing are reported on stderr with a nonzero exit while the
            // surviving containers are still returned, so the payload is read regardless of the exit code.
            Collect(result.StandardOutput, batch, inspected);
        }

        return inspected;
    }

    private static void Collect(string output, IReadOnlyList<string> requested,
        Dictionary<string, JsonElement> inspected)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray()) Add(element, requested, inspected);
            }
            else
            {
                Add(document.RootElement, requested, inspected);
            }
        }
        catch (JsonException)
        {
            // A listing must not fail because inspect output could not be read.
        }
    }

    private static void Add(JsonElement element, IReadOnlyList<string> requested,
        Dictionary<string, JsonElement> inspected)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("Id", out var id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 } value)
        {
            return;
        }

        // Inspect reports the full digest while WSLC 2.9.10 lists abbreviated IDs, so results are keyed
        // back to the identifier the caller asked about.
        inspected[requested.FirstOrDefault(candidate => MatchesId(value, candidate)) ?? value] = element.Clone();
    }

    public async Task<JsonElement> InspectAsync(string idOrName, CancellationToken ct)
    {
        var container = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        var result = await RunAsync(["container", "inspect", container.Id, "--format", "json"], ct)
            .ConfigureAwait(false);
        if (JsonNode.Parse(result.StandardOutput) is not (JsonArray and [JsonObject inspectObject]))
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"WSLC returned an invalid inspect response for container {container.Id}.");
        }

        // WSLC reports bound ports at the top level. Docker clients, including Testcontainers,
        // read the same map from NetworkSettings.Ports.
        var networkSettings = inspectObject["NetworkSettings"] as JsonObject ?? [];
        networkSettings["Ports"] ??= inspectObject["Ports"]?.DeepClone() ?? new JsonObject();
        inspectObject["NetworkSettings"] = networkSettings;
        if (inspectObject["Name"] is JsonValue name && name.TryGetValue<string>(out var value)
            && !string.IsNullOrWhiteSpace(value) && !value.StartsWith('/'))
        {
            inspectObject["Name"] = "/" + value;
        }

        return JsonSerializer.SerializeToElement(inspectObject);
    }

    private async Task<WslcCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"wslc {string.Join(' ', command)} failed: {detail.Trim()}");
        }

        return result;
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Clone()
            : throw new DockerApiException(StatusCodes.Status500InternalServerError,
                "WSLC returned an invalid container list response.");
    }

    /// <summary>
    /// Matches either direction of an ID prefix. WSLC 2.9.10 abbreviates listed IDs, while create and Docker
    /// clients use the full digest, so neither side can be assumed to be the longer one.
    /// </summary>
    private static bool MatchesId(string listedId, string requested)
    {
        var candidate = requested.Trim();
        return candidate.Length > 0
               && (listedId.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(listedId, StringComparison.OrdinalIgnoreCase));
    }

    private static DockerApiException NoSuchContainer(string idOrName) => new(StatusCodes.Status404NotFound,
        $"No such container: {idOrName}");
}

internal readonly record struct WslcCatalogContainer(
    string Id,
    string Name,
    string Image,
    string DockerState,
    long CreatedAt,
    string Status);
