using System.Globalization;

namespace Mpa.Processor.Core;

public sealed partial class CatalogFactsSnapshot
{
    // Bounded scalar accounting over the same WAL observation, without a library list.
    // Sixfold UTF-8 text expansion plus membership, SQLite/index/rollback overhead. This
    // reserves generated metadata only; no original bytes enter the private capture.
    internal (long Count, long Bytes) StagingEstimate()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        using var command = Command($"""
            SELECT count(*),coalesce(sum(2048 + 6 * (
              length(CAST(a.name AS BLOB))+length(CAST(a.account_id AS BLOB))+
              length(CAST(a.file_id AS BLOB))+length(CAST(a.mime_type AS BLOB))+
              coalesce(length(CAST(a.raw_capture AS BLOB)),0)) +
              192 * (SELECT count(*) FROM source_asset m JOIN source_binding s ON s.id=m.source_id
                WHERE m.asset_id=a.id AND s.enabled=1 AND (s.descendants=1 OR m.depth=0))),0)
            FROM discovered_asset a WHERE {DiscoveredCatalogRepository.Visible}
            """);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("Catalog staging lost its size observation.");
        var count = reader.GetInt64(0); var payload = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        if (count < 0 || payload < 0) throw new InvalidDataException("Catalog staging size observation is invalid.");
        return (count, checked(4L * 1024 * 1024 + payload * 3));
    }
}
