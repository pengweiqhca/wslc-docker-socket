namespace WslcDockerSocket.Engine;

using System.Text.Json;
using Api;

/// <summary>
/// Queries resources in the unqualified WSLC CLI scope.
/// </summary>
internal sealed class WslcCliResourceCatalog(IWslcCommandRunner runner)
{
    public async Task<IReadOnlyList<JsonElement>> ListAndInspectAsync(string resource, CancellationToken ct)
    {
        var resources = await ListAsync(resource, ct).ConfigureAwait(false);
        var inspected = new List<JsonElement>(resources.Count);
        foreach (var resourceItem in resources)
        {
            inspected.Add(await InspectAsync(resource, resourceItem, ct).ConfigureAwait(false));
        }

        return inspected;
    }

    public async Task<JsonElement> InspectAsync(string resource, string idOrName, CancellationToken ct)
    {
        var matches = (await ListAsync(resource, ct).ConfigureAwait(false))
            .Where(candidate => candidate.Id.StartsWith(idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var match = matches.Length switch
        {
            0 => throw new DockerApiException(StatusCodes.Status404NotFound, $"No such {resource}: {idOrName}"),
            1 => matches[0],
            _ => throw new DockerApiException(StatusCodes.Status409Conflict,
                $"{resource} identifier '{idOrName}' is ambiguous."),
        };

        return await InspectAsync(resource, match, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<WslcCatalogResource>> ListAsync(string resource, CancellationToken ct)
    {
        var result = await RunAsync([resource, "list", "--format", "json"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        var resources = new List<WslcCatalogResource>();
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DockerApiException(StatusCodes.Status500InternalServerError,
                    $"WSLC returned an invalid {resource} list response.");
            }

            var name = GetString(document.RootElement, "Name");
            var id = GetString(document.RootElement, "Id") ?? GetString(document.RootElement, "ID") ?? name;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
            {
                resources.Add(new WslcCatalogResource(id, name));
            }
        }

        return resources;
    }

    private async Task<JsonElement> InspectAsync(string resource, WslcCatalogResource item, CancellationToken ct)
    {
        var result = await RunAsync([resource, "inspect", item.Name, "--format", "json"], ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"WSLC returned an invalid inspect response for {resource} {item.Name}.");
        }

        return document.RootElement[0].Clone();
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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal readonly record struct WslcCatalogResource(string Id, string Name);
