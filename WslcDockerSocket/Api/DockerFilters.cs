namespace WslcDockerSocket.Api;

using System.Text.Json;

/// <summary>
/// Parses Docker's <c>filters</c> query parameter.
/// </summary>
/// <remarks>
/// Filters that cannot be applied are rejected instead of ignored. Returning an unfiltered superset is unsafe:
/// clients that enumerate and then delete — such as Testcontainers' resource reaper, which selects containers by
/// its own session label — would act on every resource on the machine rather than the ones they own.
/// </remarks>
internal sealed class DockerFilters
{
    private static readonly DockerFilters Empty = new([]);
    private readonly Dictionary<string, string[]> _values;

    private DockerFilters(Dictionary<string, string[]> values) => _values = values;

    public bool IsEmpty => _values.Count == 0;

    public static DockerFilters FromQuery(IQueryCollection query, params string[] supported)
    {
        ArgumentNullException.ThrowIfNull(query);
        var raw = query["filters"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DockerApiException(StatusCodes.Status400BadRequest, "Invalid filters query parameter.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = ReadValues(property.Value, property.Name);
            }
        }
        catch (JsonException)
        {
            throw new DockerApiException(StatusCodes.Status400BadRequest, "Invalid filters query parameter.");
        }

        foreach (var name in values.Keys)
        {
            if (!supported.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new DockerApiException(StatusCodes.Status501NotImplemented,
                    $"The '{name}' filter is not supported by the WSLC Docker socket.");
            }
        }

        return new DockerFilters(values);
    }

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    /// <summary>
    /// Applies Docker's label semantics: <c>key</c> matches on presence, <c>key=value</c> on an exact value, and
    /// every label filter must match.
    /// </summary>
    public bool MatchesLabels(JsonElement labels)
    {
        foreach (var expression in Values("label"))
        {
            var separator = expression.IndexOf('=');
            var key = separator < 0 ? expression : expression[..separator];
            var expected = separator < 0 ? null : expression[(separator + 1)..];
            if (!TryGetLabel(labels, key, out var actual)) return false;
            if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>Matches an identifier prefix, the way Docker accepts abbreviated identifiers.</summary>
    public bool MatchesIdPrefix(string name, string? actual)
    {
        var values = Values(name);
        return values.Count == 0
               || (actual is { Length: > 0 }
                   && values.Any(value => actual.StartsWith(value, StringComparison.OrdinalIgnoreCase)
                                          || value.StartsWith(actual, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Matches a substring, the way Docker matches container and resource names.</summary>
    public bool MatchesSubstring(string name, string? actual)
    {
        var values = Values(name);
        return values.Count == 0
               || (actual is not null
                   && values.Any(value => actual.Contains(value.TrimStart('/'), StringComparison.OrdinalIgnoreCase)));
    }

    public bool MatchesExact(string name, string? actual)
    {
        var values = Values(name);
        return values.Count == 0
               || values.Any(value => string.Equals(value, actual, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Matches Docker's boolean filters, such as an image listing's <c>dangling</c>.</summary>
    public bool MatchesBoolean(string name, bool actual)
    {
        var values = Values(name);
        if (values.Count == 0) return true;
        foreach (var value in values)
        {
            var expected = value switch
            {
                "1" or "true" or "True" => true,
                "0" or "false" or "False" => false,
                _ => throw new DockerApiException(StatusCodes.Status400BadRequest,
                    $"Invalid '{name}' filter value '{value}'."),
            };
            if (expected == actual) return true;
        }

        return false;
    }

    private static bool TryGetLabel(JsonElement labels, string key, out string? value)
    {
        value = null;
        if (labels.ValueKind != JsonValueKind.Object) return false;
        foreach (var label in labels.EnumerateObject())
        {
            if (!label.Name.Equals(key, StringComparison.Ordinal)) continue;
            value = label.Value.ValueKind == JsonValueKind.String ? label.Value.GetString() : null;
            return true;
        }

        return false;
    }

    private static string[] ReadValues(JsonElement value, string name) => value.ValueKind switch
    {
        // Docker accepts a list of values and also the older map-of-flags form.
        JsonValueKind.Array => [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)],
        JsonValueKind.Object => [.. value.EnumerateObject()
            .Where(flag => flag.Value.ValueKind != JsonValueKind.False)
            .Select(flag => flag.Name)],
        JsonValueKind.String => [value.GetString()!],
        _ => throw new DockerApiException(StatusCodes.Status400BadRequest, $"Invalid '{name}' filter value."),
    };
}
