namespace WslcDockerSocket.Engine;

using System.Text.Json;
using System.Text.Json.Nodes;
using Api;

/// <summary>
/// Queries containers in the unqualified WSLC CLI scope.
/// </summary>
internal sealed class WslcCliContainerCatalog(IWslcCommandRunner runner)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<WslcCatalogContainer>> ListAsync(CancellationToken ct)
    {
        var result = await RunAsync(["container", "list", "-a", "--format", "json"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        WslcContainerListItem?[] items;
        if (lines.Length > 1)
        {
            items = [.. lines.Select(ParseListItem)];
        }
        else
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            items = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => [.. document.RootElement.EnumerateArray()
                    .Select(element => element.Deserialize<WslcContainerListItem>(JsonOptions))],
                JsonValueKind.Object => [document.RootElement.Deserialize<WslcContainerListItem>(JsonOptions)],
                _ => throw new DockerApiException(StatusCodes.Status500InternalServerError,
                    "WSLC returned an invalid container list response."),
            };
        }

        return [.. items.OfType<WslcContainerListItem>().Where(container => !string.IsNullOrWhiteSpace(container.Id))
            .Select(container => new WslcCatalogContainer(
                container.Id,
                container.Name,
                container.Image,
                container.State,
                container.CreatedAt))];
    }

    public async Task<WslcCatalogContainer> ResolveAsync(string idOrName, CancellationToken ct)
    {
        var matches = (await ListAsync(ct).ConfigureAwait(false))
            .Where(container => container.Id.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)
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

    public async Task<JsonElement> InspectAsync(string idOrName, CancellationToken ct)
    {
        var container = await ResolveAsync(idOrName, ct).ConfigureAwait(false);
        var result = await RunAsync(["container", "inspect", container.Id, "--format", "json"], ct)
            .ConfigureAwait(false);
        var inspect = JsonNode.Parse(result.StandardOutput) as JsonArray;
        if (inspect is null || inspect.Count != 1 || inspect[0] is not JsonObject inspectObject)
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"WSLC returned an invalid inspect response for container {container.Id}.");
        }

        // WSLC reports bound ports at the top level. Docker clients, including Testcontainers,
        // read the same map from NetworkSettings.Ports.
        var networkSettings = inspectObject["NetworkSettings"] as JsonObject ?? new JsonObject();
        networkSettings["Ports"] ??= inspectObject["Ports"]?.DeepClone() ?? new JsonObject();
        inspectObject["NetworkSettings"] = networkSettings;
        if (inspectObject["Name"] is JsonValue name && name.TryGetValue<string>(out var value)
            && !string.IsNullOrWhiteSpace(value) && !value.StartsWith("/", StringComparison.Ordinal))
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

    private static WslcContainerListItem? ParseListItem(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Deserialize<WslcContainerListItem>(JsonOptions)
            : throw new DockerApiException(StatusCodes.Status500InternalServerError,
                "WSLC returned an invalid container list response.");
    }

    private static DockerApiException NoSuchContainer(string idOrName) => new(StatusCodes.Status404NotFound,
        $"No such container: {idOrName}");

    private sealed class WslcContainerListItem
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Image { get; init; } = string.Empty;

        public int State { get; init; }

        public long CreatedAt { get; init; }
    }
}

internal readonly record struct WslcCatalogContainer(
    string Id,
    string Name,
    string Image,
    int State,
    long CreatedAt)
{
    public string DockerState => State switch
    {
        1 => "created",
        2 => "running",
        3 => "exited",
        _ => "exited",
    };
}
