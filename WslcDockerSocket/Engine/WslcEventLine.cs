namespace WslcDockerSocket.Engine;

using System.Globalization;

/// <summary>
/// Parses one line of <c>wslc events</c> text output into the fields Docker's <c>GET /events</c> JSON response
/// needs. A line looks like:
/// <c>2026-10-08T10:31:08.000000000+08:00 container start 4788142ddfcc... (image=mysql, name=mysql)</c>.
/// </summary>
internal readonly record struct WslcEventLine(
    DateTimeOffset Time,
    string Type,
    string Action,
    string ActorId,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Unix epoch nanoseconds, to the 100ns precision .NET's own timestamp type can represent.</summary>
    public long UnixTimeNanoseconds => (Time.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100;

    /// <summary>
    /// Returns false for a blank line or one that doesn't match the expected shape, rather than throwing: a
    /// single line this can't make sense of (for example a future wslc release reformatting the text) shouldn't
    /// abort an otherwise-healthy event stream, the same tolerance the container list reader already affords
    /// WSLC's evolving CLI output.
    /// </summary>
    public static bool TryParse(string line, out WslcEventLine parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parenStart = line.IndexOf('(');
        var head = (parenStart < 0 ? line : line[..parenStart]).Trim();
        var attributesText = parenStart < 0 ? null : line[(parenStart + 1)..].TrimEnd().TrimEnd(')');

        var parts = head.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(attributesText))
        {
            foreach (var pair in attributesText.Split(',', StringSplitOptions.TrimEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    return false;
                }

                attributes[pair[..separator]] = pair[(separator + 1)..];
            }
        }

        parsed = new WslcEventLine(time, parts[1], parts[2], parts[3], attributes);
        return true;
    }
}
