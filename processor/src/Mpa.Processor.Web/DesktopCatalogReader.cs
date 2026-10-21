using System.Globalization;
using Mpa.Processor.Core;

namespace Mpa.Processor.Web;

public sealed class InvalidDesktopCatalogQueryException() : Exception("The gallery position is invalid for this account filter. Return to Photos or choose the date again.");
public sealed record DesktopCatalogPhoto(Guid Id, string AccountId, string Name, string MimeType, int? Width, int? Height,
    long? EffectiveUtc, string DateProvenance, string? RawCapture);
public sealed record DesktopCatalogPage(long DiscoveryRevision, long ConfigurationRevision, IReadOnlyList<DesktopCatalogPhoto> Photos,
    string? OlderCursor, string? NewerCursor);

/// <summary>Bounded read-only gallery adapter. All account/source memberships and canonical
/// identities come from the serial local index; opening Photos never scans Google Drive.</summary>
public sealed class DesktopCatalogReader(ProcessorStateCoordinator state)
{
    private readonly ProcessorStateCoordinator _state = state ?? throw new ArgumentNullException(nameof(state));
    public Task<DesktopCatalogPage> ReadAsync(string? accountId, string? position, bool newer, long? dateUtc, CancellationToken cancellationToken)
    {
        if (accountId is not null && (accountId.Length is < 1 or > 256 || !accountId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) ||
            position is not null && dateUtc is not null || newer && position is null || dateUtc is < -62135596800000L or > 253402300799999L)
            throw new InvalidDesktopCatalogQueryException();
        var cursor = position is null ? null : Decode(position, accountId);
        return _state.ExecuteAsync(database =>
        {
            var repository = new DiscoveredCatalogRepository(database);
            var page = dateUtc is { } date ? repository.SeekDate(date, accountId: accountId) : repository.ReadPage(cursor, before: newer, accountId: accountId);
            // The coordinator retains its serial lease across these bounded reads. No
            // scanner can change memberships/metadata between the page and edge probes.
            var first = page.Assets.Count == 0 ? null : page.Assets[0]; var last = page.Assets.Count == 0 ? null : page.Assets[^1];
            var older = last is not null && repository.ReadPage(Key(last), limit: 1, accountId: accountId).Assets.Count != 0 ? Encode(Key(last), accountId) : null;
            var newerPosition = first is not null && repository.ReadPage(Key(first), limit: 1, before: true, accountId: accountId).Assets.Count != 0 ? Encode(Key(first), accountId) : null;
            return new DesktopCatalogPage(page.DiscoveryRevision, page.ConfigurationRevision, page.Assets.Select(asset => new DesktopCatalogPhoto(asset.Id,
                asset.AccountId, asset.Facts.Name, asset.Facts.MimeType, asset.Facts.Width, asset.Facts.Height, asset.EffectiveUtc,
                asset.DateProvenance.ToString(), asset.Facts.RawCapture)).ToArray(), older, newerPosition);
        }, cancellationToken);
    }
    public Task<CatalogAsset?> ResolveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) throw new InvalidDesktopCatalogQueryException();
        return _state.ExecuteAsync(database => new DiscoveredCatalogRepository(database).ResolveVisible(id), cancellationToken);
    }
    private static CatalogCursor Key(CatalogAsset asset) => new(asset.EffectiveUtc, asset.Id);
    private static string Encode(CatalogCursor cursor, string? accountId) => string.Join('|', "1", accountId ?? "*",
        cursor.EffectiveUtc?.ToString(CultureInfo.InvariantCulture) ?? "unknown", cursor.Id.ToString("D"));
    private static CatalogCursor Decode(string value, string? accountId)
    {
        if (value.Length > 340) throw new InvalidDesktopCatalogQueryException();
        var fields = value.Split('|');
        if (fields.Length != 4 || fields[0] != "1" || fields[1] != (accountId ?? "*") || !Guid.TryParseExact(fields[3], "D", out var id) || id == Guid.Empty || id.ToString("D") != fields[3])
            throw new InvalidDesktopCatalogQueryException();
        long? date = null;
        if (fields[2] != "unknown")
        {
            if (!long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) || parsed is < -62135596800000L or > 253402300799999L || parsed.ToString(CultureInfo.InvariantCulture) != fields[2])
                throw new InvalidDesktopCatalogQueryException();
            date = parsed;
        }
        return new(date, id);
    }
}
