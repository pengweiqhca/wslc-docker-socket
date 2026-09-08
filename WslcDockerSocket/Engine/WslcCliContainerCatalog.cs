namespace WslcDockerSocket.Engine;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api;

/// <summary>
/// Queries the WSLC CLI catalog, which is the only currently available global container discovery surface.
/// </summary>
internal sealed class WslcCliContainerCatalog(string adapterSessionName)
{
    private const string ExecutablePath = "wslc";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<WslcCatalogContainer>> ListAsync(CancellationToken ct)
    {
        var containers = new Dictionary<string, WslcCatalogContainer>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in await ListScopeAsync(sessionName: null, optional: false, ct).ConfigureAwait(false))
        {
            containers[container.Id] = container;
        }

        var sessionNames = await ListSessionNamesAsync(ct).ConfigureAwait(false);
        foreach (var sessionName in sessionNames.Append(adapterSessionName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var container in await ListScopeAsync(sessionName, optional: true, ct).ConfigureAwait(false))
            {
                containers[container.Id] = container;
            }
        }

        return [.. containers.Values];
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
        var result = await RunAsync(container.SessionName, ["container", "inspect", container.Id, "--format", "json"], ct)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"WSLC returned an invalid inspect response for container {container.Id}.");
        }

        return document.RootElement[0].Clone();
    }

    private async Task<IReadOnlyList<WslcCatalogContainer>> ListScopeAsync(string? sessionName, bool optional,
        CancellationToken ct)
    {
        using CancellationTokenSource? sessionTimeout = optional
            ? new CancellationTokenSource(TimeSpan.FromSeconds(10))
            : null;
        using CancellationTokenSource? sessionCancellation = sessionTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, sessionTimeout.Token);
        var sessionToken = sessionCancellation?.Token ?? ct;

        CliResult result;
        try
        {
            result = await RunAsync(sessionName, ["list", "-a", "--format", "json"], sessionToken).ConfigureAwait(false);
        }
        catch (DockerApiException exception) when (optional && IsUnavailableSession(exception))
        {
            // A session can disappear after cataloging or be inaccessible to this process; neither has
            // containers that the adapter can expose through its current security context.
            return [];
        }
        catch (OperationCanceledException) when (optional && !ct.IsCancellationRequested)
        {
            // One stalled optional scope must not indefinitely block global Docker API reads.
            return [];
        }

        // Some empty WSLC sessions exit successfully but emit no JSON rather than `[]`.
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return [];
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var items = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray()
                .Select(element => element.Deserialize<WslcContainerListItem>(JsonOptions))
                .OfType<WslcContainerListItem>(),
            JsonValueKind.Object => [document.RootElement.Deserialize<WslcContainerListItem>(JsonOptions)
                ?? throw new DockerApiException(StatusCodes.Status500InternalServerError,
                    "WSLC returned an invalid container list response.")],
            _ => throw new DockerApiException(StatusCodes.Status500InternalServerError,
                "WSLC returned an invalid container list response."),
        };
        return [.. items.Where(container => container is not null && !string.IsNullOrWhiteSpace(container.Id))
            .Select(container => new WslcCatalogContainer(
                container!.Id,
                container.Name,
                container.Image,
                container.State,
                container.CreatedAt,
                sessionName))];
    }

    private static async Task<IReadOnlyList<string>> ListSessionNamesAsync(CancellationToken ct)
    {
        var result = await RunAsync(sessionName: null, ["system", "session", "list"], ct).ConfigureAwait(false);
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= 1)
        {
            return [];
        }

        return [.. lines.Skip(1)
            .Select(ParseSessionDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)];
    }

    private static string? ParseSessionDisplayName(string line)
    {
        // The CLI uses a whitespace table: ID, creator PID, then a potentially space-containing display name.
        var columns = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return columns.Length == 3 ? columns[2] : null;
    }

    private static bool IsUnavailableSession(DockerApiException exception) =>
        exception.Message.Contains("WSLC_E_SESSION_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("ERROR_ELEVATION_REQUIRED", StringComparison.OrdinalIgnoreCase);

    private static async Task<CliResult> RunAsync(string? sessionName, IReadOnlyList<string> command,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(sessionName, command),
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new DockerApiException(StatusCodes.Status500InternalServerError, "Failed to start wslc.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new DockerApiException(StatusCodes.Status503ServiceUnavailable,
                $"Unable to start '{ExecutablePath}': {exception.Message}");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
        var standardError = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"wslc {string.Join(' ', command)} failed: {detail.Trim()}");
        }

        return new CliResult(output, error);
    }

    private static ProcessStartInfo CreateStartInfo(string? sessionName, IReadOnlyList<string> command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(sessionName);
        }

        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
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

    private readonly record struct CliResult(string StandardOutput, string StandardError);
}

internal readonly record struct WslcCatalogContainer(
    string Id,
    string Name,
    string Image,
    int State,
    long CreatedAt,
    string? SessionName)
{
    public string DockerState => State switch
    {
        1 => "created",
        2 => "running",
        3 => "exited",
        _ => "exited",
    };
}
