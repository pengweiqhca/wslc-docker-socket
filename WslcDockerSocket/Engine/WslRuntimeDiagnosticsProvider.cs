using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// <summary>
/// Reads the runtime facts Docker clients display for the WSLC-backed Linux environment.
/// Probe failures are intentionally represented by empty/zero values so diagnostics never make /info fail.
/// </summary>
internal sealed partial class WslRuntimeDiagnosticsProvider
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly Lazy<Task<WslRuntimeDiagnostics>> _diagnostics = new(ProbeAsync);

    public Task<WslRuntimeDiagnostics> GetAsync() => _diagnostics.Value;

    private static async Task<WslRuntimeDiagnostics> ProbeAsync()
    {
        var wslcVersionTask = RunAsync("wslc", ["version"]);
        var kernelVersionTask = RunAsync("wsl.exe", ["--exec", "uname", "-r"]);
        var memoryTask = RunAsync("wsl.exe", ["--exec", "sh", "-c", "grep '^MemTotal:' /proc/meminfo"]);
        await Task.WhenAll(wslcVersionTask, kernelVersionTask, memoryTask).ConfigureAwait(false);

        return new WslRuntimeDiagnostics(
            ParseWslcVersion(wslcVersionTask.Result),
            FirstNonEmptyLine(kernelVersionTask.Result),
            ParseMemTotal(memoryTask.Result),
            $"{RuntimeInformation.OSDescription.Trim()} with WSL Containers");
    }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments),
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                return string.Empty;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return string.Empty;
            }

            var output = await standardOutput.ConfigureAwait(false);
            await standardError.ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string ParseWslcVersion(string output)
    {
        var match = VersionRegex().Match(output);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static string FirstNonEmptyLine(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? string.Empty;

    private static long ParseMemTotal(string output)
    {
        var match = MemTotalRegex().Match(output);
        if (!match.Success || !long.TryParse(match.Groups["kib"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var kibibytes))
        {
            return 0;
        }

        try
        {
            return checked(kibibytes * 1024);
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"(?im)^\s*wslc\s+(?<version>\d+(?:\.\d+){1,3}(?:[-+][^\s]+)?)\s*$", RegexOptions.None, "zh-CN")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?im)^\s*MemTotal:\s*(?<kib>\d+)\s*kB\s*$", RegexOptions.None, "zh-CN")]
    private static partial Regex MemTotalRegex();
}

internal readonly record struct WslRuntimeDiagnostics(
    string ServerVersion,
    string KernelVersion,
    long MemTotal,
    string OperatingSystem);
