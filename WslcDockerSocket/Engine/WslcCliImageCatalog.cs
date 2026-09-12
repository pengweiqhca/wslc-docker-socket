namespace WslcDockerSocket.Engine;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api;

/// <summary>Maps images from the unqualified WSLC CLI scope to Docker-shaped JSON.</summary>
internal sealed class WslcCliImageCatalog(IWslcCommandRunner runner)
{
    // WSLC's list output renders sizes and timestamps for display, so exact values come from inspect,
    // which accepts many image references in a single invocation.
    private const int MaximumInspectBatchSize = 100;

    /// <summary>Lists Docker image summaries, replacing WSLC's rendered list values with exact inspect values.</summary>
    public async Task<IReadOnlyList<JsonElement>> ListAsync(CancellationToken ct)
    {
        var summaries = await ListSummariesAsync(ct).ConfigureAwait(false);
        if (summaries.Count == 0) return summaries;

        var inspected = await InspectManyAsync(summaries, ct).ConfigureAwait(false);
        return [.. summaries.Select(summary => WithExactValues(summary, inspected))];
    }

    public async Task<int> CountAsync(CancellationToken ct) => (await ListSummariesAsync(ct).ConfigureAwait(false)).Count;

    private async Task<IReadOnlyList<InspectedImage>> InspectManyAsync(IReadOnlyList<JsonElement> summaries, CancellationToken ct)
    {
        var ids = summaries.Select(summary => GetOptionalString(summary, "Id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var inspected = new List<InspectedImage>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += MaximumInspectBatchSize)
        {
            var command = new List<string> { "image", "inspect" };
            command.AddRange(ids.Skip(offset).Take(MaximumInspectBatchSize));
            command.AddRange(["--format", "json"]);
            var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            // WSLC reports images removed since the listing on stderr with a nonzero exit while still
            // emitting the images it did find, so the payload is read regardless of the exit code.
            inspected.AddRange(ReadInspectedImages(result.StandardOutput));
        }

        return inspected;
    }

    private static List<InspectedImage> ReadInspectedImages(string output)
    {
        var images = new List<InspectedImage>();
        if (string.IsNullOrWhiteSpace(output)) return images;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray()) AddInspectedImage(images, element);
            }
            else
            {
                AddInspectedImage(images, document.RootElement);
            }
        }
        catch (JsonException)
        {
            // Fall back to the values WSLC's list output already provided.
        }

        return images;
    }

    private static void AddInspectedImage(List<InspectedImage> images, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var id = GetOptionalString(element, "Id") ?? GetOptionalString(element, "ID");
        if (string.IsNullOrWhiteSpace(id)) return;
        images.Add(new InspectedImage(RemoveSha256Prefix(id), GetOptionalInt64(element, "Size"), ReadCreatedSeconds(element)));
    }

    /// <summary>Overlays exact inspect values, keeping WSLC's rendered list values when inspect omits them.</summary>
    private static JsonElement WithExactValues(JsonElement summary, IReadOnlyList<InspectedImage> inspected)
    {
        var id = GetOptionalString(summary, "Id");
        if (string.IsNullOrWhiteSpace(id)) return summary;

        // The list reports abbreviated IDs while inspect reports the full digest.
        var listedId = RemoveSha256Prefix(id);
        var match = inspected.FirstOrDefault(image => image.Id.StartsWith(listedId, StringComparison.OrdinalIgnoreCase)
            || listedId.StartsWith(image.Id, StringComparison.OrdinalIgnoreCase));
        if (match is null || (match.Size is null && match.Created <= 0)) return summary;

        var node = JsonNode.Parse(summary.GetRawText()) as JsonObject ?? throw InvalidResponse("image list");
        if (match.Size is { } size)
        {
            node["Size"] = size;
            node["VirtualSize"] = size;
        }

        if (match.Created > 0) node["Created"] = match.Created;
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed record InspectedImage(string Id, long? Size, long Created);

    private async Task<IReadOnlyList<JsonElement>> ListSummariesAsync(CancellationToken ct)
    {
        var result = await RunAsync(["image", "list", "--format", "json"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput)) return [];

        var values = new List<JsonElement>();
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            foreach (var line in lines) values.Add(ParseObject(line, "image list"));
        }
        else
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind == JsonValueKind.Array) values.AddRange(document.RootElement.EnumerateArray().Select(element => element.Clone()));
            else if (document.RootElement.ValueKind == JsonValueKind.Object) values.Add(document.RootElement.Clone());
            else throw InvalidResponse("image list");
        }

        return [.. values.Select(MapListImage)];
    }

    public async Task<JsonElement> InspectAsync(string image, CancellationToken ct)
    {
        var imageId = await ResolveImageIdAsync(image, ct).ConfigureAwait(false);
        var result = await RunAsync(["image", "inspect", imageId, "--format", "json"], ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var value = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array when document.RootElement.GetArrayLength() == 1 => document.RootElement[0],
            JsonValueKind.Object => document.RootElement,
            _ => throw InvalidResponse($"image inspect for {image}"),
        };
        return MapImage(value, $"image inspect for {image}");
    }

    private async Task<string> ResolveImageIdAsync(string image, CancellationToken ct)
    {
        var requestedReference = DockerImageReference.Parse(image);
        var matchingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolution needs only IDs and tags; enriching sizes here would inspect every image on every lookup.
        foreach (var candidate in await ListSummariesAsync(ct).ConfigureAwait(false))
        {
            var id = GetRequiredString(candidate, "Id", "ID", "image list ID");
            if (MatchesImageId(image, id) || HasMatchingRepoTag(candidate, requestedReference))
            {
                matchingIds.Add(id);
            }
        }

        return matchingIds.Count switch
        {
            1 => matchingIds.Single(),
            0 => throw new DockerApiException(StatusCodes.Status404NotFound, $"No such image: {image}"),
            _ => throw new DockerApiException(StatusCodes.Status409Conflict, $"Image reference is ambiguous: {image}"),
        };
    }

    private static bool MatchesImageId(string requestedImage, string candidateId)
    {
        var requested = RemoveSha256Prefix(requestedImage.Trim());
        var candidate = RemoveSha256Prefix(candidateId);
        return candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingRepoTag(JsonElement image, DockerImageReference requestedReference)
    {
        if (!TryGetProperty(image, "RepoTags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return tags.EnumerateArray().Any(tag => tag.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(tag.GetString())
            && requestedReference.Matches(tag.GetString()!));
    }

    private static string RemoveSha256Prefix(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        ? value["sha256:".Length..]
        : value;

    public async Task PullAsync(string image, CancellationToken ct)
    {
        var result = await runner.RunAsync(["image", "pull", image], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new DockerApiException(StatusCodes.Status500InternalServerError, $"wslc image pull failed: {detail.Trim()}");
        }
    }

    private async Task<WslcCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new DockerApiException(StatusCodes.Status500InternalServerError, $"wslc {string.Join(' ', command)} failed: {detail.Trim()}");
        }

        return result;
    }

    private static JsonElement MapListImage(JsonElement image)
    {
        var id = GetRequiredString(image, "Id", "ID", "image list ID");
        var size = ReadSizeBytes(image);
        var node = new JsonObject
        {
            ["Id"] = id,
            ["ParentId"] = string.Empty,
            ["RepoTags"] = ToRepoTags(image),
            ["RepoDigests"] = new JsonArray(),
            ["Created"] = ReadCreatedSeconds(image),
            ["Size"] = size,
            ["VirtualSize"] = size,
            ["SharedSize"] = -1,
            ["Labels"] = null,
            // WSLC's list value is display-oriented text rather than Docker's numeric count.
            ["Containers"] = -1,
        };
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Reads Docker's numeric creation time from either a numeric field or WSLC's rendered timestamp.</summary>
    private static long ReadCreatedSeconds(JsonElement image)
    {
        if (GetOptionalInt64(image, "Created") is { } numeric) return numeric;
        var text = GetOptionalString(image, "Created") ?? GetOptionalString(image, "CreatedAt");
        return TryParseTimestamp(text, out var seconds) ? seconds : 0;
    }

    private static bool TryParseTimestamp(string? text, out long unixSeconds) =>
        WslcNativeFormat.TryParseUnixSeconds(text, out unixSeconds);

    /// <summary>Reads Docker's byte count from either a numeric field or WSLC's humanized size text.</summary>
    private static long ReadSizeBytes(JsonElement image)
    {
        if (GetOptionalInt64(image, "Size") is { } numeric) return numeric;
        return WslcNativeFormat.TryParseHumanizedSize(GetOptionalString(image, "Size"), out var bytes) ? bytes : 0;
    }

    private static JsonArray ToRepoTags(JsonElement image)
    {
        if (TryGetProperty(image, "RepoTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString()))
                {
                    result.Add(tag.GetString());
                }
            }

            return result;
        }

        var name = GetOptionalString(image, "Name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return new JsonArray(name);
        }

        var repository = GetOptionalString(image, "Repository");
        var tagName = GetOptionalString(image, "Tag");
        return string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(tagName)
            || tagName.Equals("<none>", StringComparison.OrdinalIgnoreCase)
            ? new JsonArray()
            : new JsonArray($"{repository}:{tagName}");
    }

    private static string GetRequiredString(JsonElement element, string firstName, string secondName, string operation) =>
        GetOptionalString(element, firstName) ?? GetOptionalString(element, secondName)
            ?? throw InvalidResponse($"{operation}: missing image ID");

    private static string? GetOptionalString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetOptionalInt64(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
            ? result
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

    private static JsonElement MapImage(JsonElement image, string operation)
    {
        var node = JsonNode.Parse(image.GetRawText()) as JsonObject ?? throw InvalidResponse(operation);
        NormalizeId(node, operation);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void NormalizeId(JsonObject node, string operation)
    {
        if (node["Id"] is not null) return;
        if (node["ID"] is JsonNode id) { node["Id"] = id.DeepClone(); return; }
        throw InvalidResponse($"{operation}: missing image ID");
    }

    private static bool TryGetString(JsonObject node, string name, out string? value)
    {
        value = node[name]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static JsonElement ParseObject(string json, string operation)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : throw InvalidResponse(operation);
    }

    private static DockerApiException InvalidResponse(string operation) => new(StatusCodes.Status500InternalServerError, $"WSLC returned an invalid {operation} response.");
}
