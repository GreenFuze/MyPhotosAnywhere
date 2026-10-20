using System.Globalization;
using System.Text.RegularExpressions;

namespace Mpa.Processor.Core;

public sealed record DiscoveryTimestamp(string? Raw, long? UtcMilliseconds);

/// <summary>Preserves an unqualified EXIF wall time without inventing a timezone. Only an
/// explicit RFC3339 offset/Z produces an instant; later verified metadata can refine it.</summary>
public static partial class DiscoveryTimestampReader
{
    public static DiscoveryTimestamp Read(string? raw)
    {
        if (raw is null) return new(null, null);
        if (raw.Length > 128 || raw.Any(char.IsControl)) throw new ArgumentException("Discovery timestamp exceeds its supported bound.", nameof(raw));
        if (!OffsetTimestamp().IsMatch(raw) || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return new(raw, null);
        return new(raw, instant.ToUnixTimeMilliseconds());
    }
    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetTimestamp();
}
