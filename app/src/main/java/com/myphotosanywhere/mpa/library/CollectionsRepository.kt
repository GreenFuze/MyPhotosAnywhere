package com.myphotosanywhere.mpa.library

import androidx.room.InvalidationTracker
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.withContext

internal data class CollectionPreview(val count: Int,val entries: List<TimelineEntry>,val paths: List<String> = entries.map { it.thumbnailPath },val coverIds: List<String?> = entries.map { it.photoId })

/** Cards observe invalidation, then fetch at most four covers; never subscribe to the media library. */
internal class CollectionsRepository(private val database: PhotoDatabase,private val timeline: TimelineRepository) {
    private fun changes() = callbackFlow {
        val observer=object: InvalidationTracker.Observer("photos","cloud_photos","drive_sources","album_photos","faces","people","drive_trash","photo_people","person_aliases","device_media") {
            override fun onInvalidated(tables: Set<String>) { trySend(Unit) }
        }
        database.invalidationTracker.addObserver(observer);trySend(Unit)
        awaitClose { database.invalidationTracker.removeObserver(observer) }
    }.conflate()
    fun preview(query: TimelineQuery): Flow<CollectionPreview> = changes().map { withContext(Dispatchers.IO) {
        val entries=if(query.filter=="Offline") {
            // A cover is an optional sample. Never walk an archive of stale paths to fill four cells.
            database.openHelper.readableDatabase.query(TimelineSql(query).page(null,TimelineDirection.AFTER,24)).use { rows ->
                buildList { while(rows.moveToNext()) {
                    val entry=timeline.read(rows)
                    if(size<4 && timeline.accepts(query,entry)) add(entry)
                } }
            }
        } else timeline.page(query,null,TimelineDirection.AFTER,4)
        // Offline availability is a filesystem property: do not invent a SQL count from cached paths.
        val count=if(query.album==null) -1 else database.openHelper.readableDatabase.query(
            "SELECT COUNT(*) FROM album_photos a JOIN photos p ON p.id=a.photoId WHERE a.albumId=? AND p.archived=0 AND ${com.myphotosanywhere.mpa.trash.TrashVisibility.PHOTO_SQL}",arrayOf(query.album)).use { rows -> check(rows.moveToFirst());rows.getInt(0) }
        CollectionPreview(count,entries)
    } }
    /** Existing local annotations only; this never starts recognition or contacts a map service. */
    fun special(kind: String): Flow<CollectionPreview> = changes().map { withContext(Dispatchers.IO) {
        require(kind in setOf("People","Places","Trash"))
        if(kind=="People") {
            val covers=database.openHelper.readableDatabase.query(TimelineSql(TimelineQuery()).peopleOverview(coversOnly=true)).use { rows ->
                buildList { while(rows.moveToNext()) rows.getString(3)?.takeIf { it.isNotBlank() }?.let { add(it to rows.getString(4)) } }
            }
            return@withContext CollectionPreview(-1,emptyList(),covers.map { it.first },covers.map { it.second })
        }
        val visible=com.myphotosanywhere.mpa.trash.TrashVisibility.PHOTO_SQL
        val sql=when(kind) {
            "Places" -> "SELECT p.thumbnailPath,p.id FROM photos p WHERE p.archived=0 AND p.latitude BETWEEN -90 AND 90 AND p.longitude BETWEEN -180 AND 180 AND p.thumbnailPath<>'' AND $visible ORDER BY COALESCE(p.capturedAt,p.importedAt) DESC,p.id LIMIT 4"
            else -> "SELECT thumbnailPath,NULL FROM drive_trash WHERE confirmed=1 AND thumbnailPath<>'' ORDER BY updatedAt DESC,key LIMIT 4"
        }
        val paths=database.openHelper.readableDatabase.query(sql).use { rows -> buildList { while(rows.moveToNext()) add(rows.getString(0) to rows.getString(1)) } }
        CollectionPreview(-1,emptyList(),paths.map { it.first },paths.map { it.second })
    } }
}
