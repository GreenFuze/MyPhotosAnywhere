package com.myphotosanywhere.mpa.timeline

import androidx.sqlite.db.SimpleSQLiteQuery

/** One global ordering and one visibility policy for paging, seeking and viewer snapshots. */
internal class TimelineSql(private val query: TimelineQuery) {
    private val arguments=arrayOf<Any?>(query.source,query.filter,query.album,query.person,query.type.name,query.fromMillis,query.untilMillis,if(query.deviceOnly || query.deviceFolder!=null) 1 else 0,query.deviceFolder?.volume,query.deviceFolder?.path)
    private val projection="""
        WITH connected AS (
          SELECT c.* FROM cloud_photos c JOIN drive_sources s ON s.key=c.bindingKey
          WHERE s.enabled=1 AND (?1 IS NULL OR c.bindingKey=?1)
          AND NOT EXISTS(SELECT 1 FROM drive_trash t WHERE t.accountId=s.accountId AND t.remoteId=c.remoteId AND t.confirmed=1)
        ), byPhoto AS (
          SELECT photoId,MAX(CASE WHEN removed=0 THEN 1 ELSE 0 END) AS live
          FROM connected WHERE photoId IS NOT NULL GROUP BY photoId
        ), byRemote AS (
          SELECT bindingKey,remoteId,MAX(CASE WHEN removed=0 THEN 1 ELSE 0 END) AS live
          FROM connected GROUP BY bindingKey,remoteId
        ), linked AS (
          SELECT p.*,
            COALESCE((SELECT c.capturedAt FROM cloud_photos c INDEXED BY index_cloud_photos_photoId WHERE c.photoId=p.id AND c.capturedAt IS NOT NULL
              AND c.bindingKey=p.sourceBindingKey AND c.remoteId=p.sourceRemoteId ORDER BY c.key COLLATE BINARY LIMIT 1),
              (SELECT c.capturedAt FROM cloud_photos c INDEXED BY index_cloud_photos_photoId WHERE c.photoId=p.id AND c.capturedAt IS NOT NULL
               ORDER BY c.key COLLATE BINARY LIMIT 1)) AS linkedCapture,
            COALESCE((SELECT c.key FROM cloud_photos c INDEXED BY index_cloud_photos_photoId
              JOIN drive_sources s ON s.key=c.bindingKey AND s.enabled=1
              WHERE c.photoId=p.id AND c.removed=0 AND (?1 IS NULL OR c.bindingKey=?1)
              AND c.bindingKey=p.sourceBindingKey AND c.remoteId=p.sourceRemoteId ORDER BY c.key COLLATE BINARY LIMIT 1),
              (SELECT c.key FROM cloud_photos c INDEXED BY index_cloud_photos_photoId
               JOIN drive_sources s ON s.key=c.bindingKey AND s.enabled=1
               WHERE c.photoId=p.id AND c.removed=0 AND (?1 IS NULL OR c.bindingKey=?1)
               ORDER BY c.key COLLATE BINARY LIMIT 1)) AS previewKey
          FROM photos p
        ), entries AS (
          SELECT 'photo:'||p.id AS canonicalKey,p.id AS photoId,c.key AS cloudKey,
            COALESCE(p.capturedAt,p.linkedCapture,CASE WHEN EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id) THEN NULLIF(p.importedAt,0) ELSE p.importedAt END) AS effectiveMillis,
            CASE WHEN p.capturedAt IS NOT NULL THEN 'CAPTURE' WHEN p.linkedCapture IS NOT NULL
              THEN 'LINKED_CAPTURE' WHEN EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id) THEN CASE WHEN p.importedAt>0 THEN 'DEVICE_ADDED' ELSE 'UNKNOWN' END ELSE 'IMPORT' END AS timestampSource,
            p.displayName AS displayName,p.mimeType AS mimeType,p.originalPath AS originalPath,
            CASE WHEN p.thumbnailPath LIKE 'content://%' AND NOT EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id AND d.available=1) THEN COALESCE(c.thumbnailPath,'') WHEN p.thumbnailPath<>'' THEN p.thumbnailPath ELSE COALESCE(c.thumbnailPath,'') END AS thumbnailPath,
            COALESCE(c.thumbnailUpdatedAt,0) AS thumbnailUpdatedAt,p.bytes AS bytes,p.sha256 AS sha256,
            c.md5 AS md5,p.cachedVersion AS version,p.sourceBindingKey AS bindingKey,p.sourceRemoteId AS remoteId
          FROM linked p LEFT JOIN connected c ON c.key=p.previewKey
          LEFT JOIN byPhoto bp ON bp.photoId=p.id
          LEFT JOIN byRemote br ON br.bindingKey=p.sourceBindingKey AND br.remoteId=p.sourceRemoteId
          WHERE (p.sourceRemoteId IS NULL OR (bp.photoId IS NULL AND br.remoteId IS NULL)
            OR bp.live=1 OR br.live=1 OR ?2 IN ('Offline','Favorites','Archive') OR ?3 IS NOT NULL OR ?4 IS NOT NULL)
          AND (p.sourceRemoteId IS NULL OR p.originalPath<>'' OR bp.live=1
            OR ?3 IS NOT NULL OR ?2 IN ('Favorites','Archive'))
          AND (?1 IS NULL OR p.sourceBindingKey=?1 OR bp.live=1)
          AND ${com.myphotosanywhere.mpa.trash.TrashVisibility.PHOTO_SQL}
          AND (p.originalPath<>'' OR bp.live=1 OR NOT EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id)
            OR EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id AND d.available=1))
          AND p.archived=CASE WHEN ?2='Archive' THEN 1 ELSE 0 END
          AND (?2<>'Favorites' OR p.favorite=1) AND (?2<>'Videos' OR p.mimeType LIKE 'video/%')
          AND (?2<>'Edits' OR p.parentId IS NOT NULL) AND (?2<>'Offline' OR p.originalPath<>'' OR EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=p.id AND d.available=1))
          AND (?3 IS NULL OR EXISTS(SELECT 1 FROM album_photos a WHERE a.albumId=?3 AND a.photoId=p.id))
          AND (?4 IS NULL OR EXISTS(SELECT 1 FROM faces f WHERE f.personId=?4 AND f.photoId=p.id) OR EXISTS(SELECT 1 FROM photo_people l WHERE l.personId=?4 AND l.photoId=p.id AND l.excluded=0))
          UNION ALL
          SELECT 'cloud:'||c.key,NULL,c.key,
            COALESCE(c.capturedAt,c.validModified),
            CASE WHEN c.capturedAt IS NOT NULL THEN 'CAPTURE' WHEN c.validModified IS NOT NULL
              THEN 'DRIVE_MODIFIED' ELSE 'UNKNOWN' END,
            c.name,c.mimeType,'',c.thumbnailPath,c.thumbnailUpdatedAt,c.bytes,c.sha256,c.md5,c.version,c.bindingKey,c.remoteId
          FROM (SELECT r.*,
            CASE WHEN r.modifiedTime GLOB '????-??-??T??:??:??*'
              AND (substr(r.modifiedTime,-1)='Z' OR substr(r.modifiedTime,-6,1) IN ('+','-'))
              AND strftime('%Y-%m-%dT%H:%M:%S',substr(r.modifiedTime,1,19),'+0 days')=substr(r.modifiedTime,1,19)
              THEN CAST(ROUND((julianday(r.modifiedTime)-2440587.5)*86400000) AS INTEGER) END AS validModified
            FROM connected r) c
          WHERE c.removed=0 AND NOT EXISTS(SELECT 1 FROM photos p WHERE p.id=c.photoId)
          AND ?2 IN ('All','Videos') AND (?2<>'Videos' OR c.mimeType LIKE 'video/%') AND ?3 IS NULL AND ?4 IS NULL
        ), filtered AS (
          SELECT * FROM entries WHERE (?5='ALL' OR (?5='PHOTOS' AND mimeType LIKE 'image/%') OR (?5='VIDEOS' AND mimeType LIKE 'video/%'))
          AND (?6 IS NULL OR effectiveMillis>=?6) AND (?7 IS NULL OR effectiveMillis<?7)
          AND (?8=0 OR EXISTS(SELECT 1 FROM device_media d WHERE d.photoId=entries.photoId AND d.available=1
            AND (?9 IS NULL OR (d.volume=?9 AND d.folderPath=?10))))
          AND ${if(query.importedPerson!=null || query.includeMetadata) "photoId IS NOT NULL" else "1"}
        )
    """.trimIndent()

    fun peopleOverview(coversOnly: Boolean=false)=statement(""", person_members AS (
        SELECT personId,photoId FROM faces WHERE personId IS NOT NULL
        UNION SELECT personId,photoId FROM photo_people WHERE excluded=0
        ), member_media AS (
          SELECT m.personId,e.photoId,e.thumbnailPath,e.effectiveMillis,e.canonicalKey
          FROM person_members m JOIN filtered e ON e.photoId=m.photoId
        )
        SELECT n.id,n.name,COUNT(m.photoId),
        COALESCE((SELECT f.cropPath FROM faces f JOIN member_media v ON v.photoId=f.photoId AND v.personId=f.personId
            WHERE f.personId=n.id AND f.cropPath<>'' ORDER BY f.id LIMIT 1),
          (SELECT v.thumbnailPath FROM member_media v WHERE v.personId=n.id
            ORDER BY v.effectiveMillis DESC,v.canonicalKey COLLATE BINARY ASC LIMIT 1),''),
        (SELECT v.photoId FROM member_media v WHERE v.personId=n.id
            ORDER BY v.effectiveMillis DESC,v.canonicalKey COLLATE BINARY ASC LIMIT 1)
        FROM people n LEFT JOIN member_media m ON m.personId=n.id
        GROUP BY n.id ${if(coversOnly) "HAVING COUNT(m.photoId)>0" else ""} ORDER BY n.name COLLATE NOCASE,n.id ${if(coversOnly) "LIMIT 4" else ""}""")

    fun dateRange()=statement("SELECT MIN(effectiveMillis),MAX(effectiveMillis) FROM filtered")
    fun ordered()=statement("SELECT * FROM filtered ORDER BY effectiveMillis DESC,canonicalKey COLLATE BINARY ASC")
    fun identity(key: String)=statement("SELECT * FROM filtered WHERE canonicalKey=?11",key)
    fun date(beforeExclusive: Long)=statement("SELECT * FROM filtered WHERE effectiveMillis<?11 ORDER BY effectiveMillis DESC,canonicalKey COLLATE BINARY ASC LIMIT 1",beforeExclusive)

    fun page(cursor: TimelineCursor?,direction: TimelineDirection,limit: Int,inclusive: Boolean=false): SimpleSQLiteQuery {
        require(limit in 1..300)
        return scan(cursor,direction,inclusive,limit)
    }

    /** A search scans one bounded cursor, never repeats a full sorted query per nonmatching batch. */
    fun scan(cursor: TimelineCursor?,direction: TimelineDirection,inclusive: Boolean=false,limit: Int?=null): SimpleSQLiteQuery {
        require(limit==null || limit in 1..300)
        val reverse=direction==TimelineDirection.BEFORE
        val predicate=if(cursor==null) "1" else if(cursor.millis==null) {
            if(reverse) "(effectiveMillis IS NOT NULL OR (effectiveMillis IS NULL AND canonicalKey COLLATE BINARY<?11))"
            else "(effectiveMillis IS NULL AND canonicalKey COLLATE BINARY${if(inclusive) ">=" else ">"}?11)"
        } else if(reverse) "(effectiveMillis>?11 OR (effectiveMillis=?11 AND canonicalKey COLLATE BINARY<?12))"
        else "(effectiveMillis IS NULL OR effectiveMillis<?11 OR (effectiveMillis=?11 AND canonicalKey COLLATE BINARY${if(inclusive) ">=" else ">"}?12))"
        val params=if(cursor==null) emptyArray() else if(cursor.millis==null) arrayOf<Any?>(cursor.key) else arrayOf<Any?>(cursor.millis,cursor.key)
        val order=if(reverse) "effectiveMillis IS NULL DESC,effectiveMillis ASC,canonicalKey COLLATE BINARY DESC" else "effectiveMillis DESC,canonicalKey COLLATE BINARY ASC"
        return statement("SELECT * FROM filtered WHERE $predicate ORDER BY $order${limit?.let { " LIMIT $it" }.orEmpty()}",*params)
    }

    private fun statement(tail: String,vararg extra: Any?): SimpleSQLiteQuery {
        val sql=if(!query.needsMetadata) tail else {
            // Enrich only the candidate cursor. The first sixteen columns retain the existing viewer-index contract.
            val order=tail.substringAfter("ORDER BY ","").substringBefore(" LIMIT ").takeIf(String::isNotBlank)
            """SELECT e.*,p.placeLabel,p.labels,COALESCE(p.capturedAt,p.importedAt),p.importedMetadata,p.favorite,p.parentId,p.latitude,p.longitude,
                (SELECT group_concat(name,' ') FROM (SELECT n.name FROM faces f JOIN people n ON n.id=f.personId WHERE f.photoId=p.id UNION SELECT n.name FROM photo_people l JOIN people n ON n.id=l.personId WHERE l.photoId=p.id AND l.excluded=0)),
                (SELECT group_concat(a.name,' ') FROM album_photos m JOIN albums a ON a.id=m.albumId WHERE m.photoId=p.id)
                FROM ($tail) e LEFT JOIN photos p ON p.id=e.photoId ${order?.let { "ORDER BY $it" }.orEmpty()}"""
        }
        return SimpleSQLiteQuery("$projection\n$sql",(arguments.toList()+extra.toList()).toTypedArray())
    }
}
