namespace WslcDockerSocket.Engine;

using System.Globalization;

/// <summary>
/// Reads the display-oriented values WSLC's list commands render for humans. WSLC 2.9.10 aligned its list
/// output with Docker's CLI, which reports timestamps and sizes as text rather than numbers.
/// </summary>
internal static class WslcNativeFormat
{
    /// <summary>Converts a rendered or numeric timestamp to Docker's Unix-seconds representation.</summary>
    public static bool TryParseUnixSeconds(string? text, out long unixSeconds)
    {
        unixSeconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // WSLC renders Go layouts such as "2006-01-02 15:04:05 -0700 MST"; .NET cannot parse the zone name.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = tokens.Length >= 3
            ? string.Join(' ', tokens[0], tokens[1], NormalizeUtcOffset(tokens[2]))
            : text.Trim();
        if (!DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && !DateTimeOffset.TryParseExact(candidate, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
        {
            return false;
        }

        unixSeconds = parsed.ToUnixTimeSeconds();
        return unixSeconds > 0;
    }

    /// <summary>Converts a humanized size such as "146MB" or "1.83GB" to a byte count.</summary>
    public static bool TryParseHumanizedSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        var digits = 0;
        while (digits < trimmed.Length && (char.IsAsciiDigit(trimmed[digits]) || trimmed[digits] == '.')) digits++;
        if (digits == 0
            || !double.TryParse(trimmed[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            return false;
        }

        // Go humanizes sizes with decimal units; binary suffixes such as MiB step by 1024 instead.
        var multiplier = trimmed[digits..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1D,
            "K" or "KB" => 1_000D,
            "M" or "MB" => 1_000_000D,
            "G" or "GB" => 1_000_000_000D,
            "T" or "TB" => 1_000_000_000_000D,
            "P" or "PB" => 1_000_000_000_000_000D,
            "KIB" => 1_024D,
            "MIB" => 1_024D * 1_024,
            "GIB" => 1_024D * 1_024 * 1_024,
            "TIB" => 1_024D * 1_024 * 1_024 * 1_024,
            "PIB" => 1_024D * 1_024 * 1_024 * 1_024 * 1_024,
            _ => -1D,
        };
        if (multiplier < 0) return false;
        var scaled = Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
        if (scaled > long.MaxValue) return false;
        bytes = (long)scaled;
        return true;
    }

    private static string NormalizeUtcOffset(string offset) =>
        offset.Length == 5 && offset[0] is '+' or '-' && offset.Skip(1).All(char.IsAsciiDigit)
            ? $"{offset[..3]}:{offset[3..]}"
            : offset;
}
