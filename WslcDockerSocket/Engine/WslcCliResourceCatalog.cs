namespace WslcDockerSocket.Engine;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Api;

/// <summary>
/// Queries WSLC resources that have a Docker-shaped CLI inspect representation but are not globally enumerable
/// through the managed SDK.
/// </summary>
internal sealed class WslcCliResourceCatalog(string adapterSessionName)
{
    private const string ExecutablePath = "wslc";

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
        var resources = new Dictionary<string, WslcCatalogResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in await ListScopeAsync(resource, sessionName: null, optional: false, ct).ConfigureAwait(false))
        {
            resources[item.Name] = item;
        }

        var sessionNames = await ListSessionNamesAsync(ct).ConfigureAwait(false);
        foreach (var sessionName in sessionNames.Append(adapterSessionName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in await ListScopeAsync(resource, sessionName, optional: true, ct).ConfigureAwait(false))
            {
                resources[item.Name] = item;
            }
        }

        return [.. resources.Values];
    }

    private async Task<IReadOnlyList<WslcCatalogResource>> ListScopeAsync(string resource, string? sessionName,
        bool optional, CancellationToken ct)
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
            result = await RunAsync(sessionName, [resource, "list", "--format", "json"], sessionToken)
                .ConfigureAwait(false);
        }
        catch (DockerApiException exception) when (optional && IsUnavailableSession(exception))
        {
            return [];
        }
        catch (OperationCanceledException) when (optional && !ct.IsCancellationRequested)
        {
            return [];
        }

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
                resources.Add(new WslcCatalogResource(id, name, sessionName));
            }
        }

        return resources;
    }

    private static async Task<IReadOnlyList<string>> ListSessionNamesAsync(CancellationToken ct)
    {
        var result = await RunAsync(sessionName: null, ["system", "session", "list"], ct).ConfigureAwait(false);
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= 1
            ? []
            : [.. lines.Skip(1).Select(ParseSessionDisplayName).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)];
    }

    private static async Task<JsonElement> InspectAsync(string resource, WslcCatalogResource item, CancellationToken ct)
    {
        var result = await RunAsync(item.SessionName, [resource, "inspect", item.Name, "--format", "json"], ct)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
        {
            throw new DockerApiException(StatusCodes.Status500InternalServerError,
                $"WSLC returned an invalid inspect response for {resource} {item.Name}.");
        }

        return document.RootElement[0].Clone();
    }

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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ParseSessionDisplayName(string line)
    {
        var columns = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return columns.Length == 3 ? columns[2] : null;
    }

    private static bool IsUnavailableSession(DockerApiException exception) =>
        exception.Message.Contains("WSLC_E_SESSION_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("ERROR_ELEVATION_REQUIRED", StringComparison.OrdinalIgnoreCase);

    private readonly record struct CliResult(string StandardOutput, string StandardError);
}

internal readonly record struct WslcCatalogResource(string Id, string Name, string? SessionName);
