package com.myphotosanywhere.mpa.library

import androidx.room.InvalidationTracker
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.withContext

internal data class CollectionPreview(val count: Int,val entries: List<TimelineEntry>,val paths: List<String> = entries.map { it.thumbnailPath })

/** Cards observe invalidation, then fetch at most four covers; never subscribe to the media library. */
internal class CollectionsRepository(private val database: PhotoDatabase,private val timeline: TimelineRepository) {
    private fun changes() = callbackFlow {
        val observer=object: InvalidationTracker.Observer("photos","cloud_photos","drive_sources","album_photos","faces","people") {
            override fun onInvalidated(tables: Set<String>) { trySend(Unit) }
        }
        database.invalidationTracker.addObserver(observer);trySend(Unit)
        awaitClose { database.invalidationTracker.removeObserver(observer) }
    }.conflate()
    fun preview(query: TimelineQuery): Flow<CollectionPreview> = changes().map { withContext(Dispatchers.IO) {
        val entries=timeline.page(query,null,TimelineDirection.AFTER,4)
        // Offline availability is a filesystem property: do not invent a SQL count from cached paths.
        val count=if(query.album==null) -1 else database.openHelper.readableDatabase.query(
            "SELECT COUNT(*) FROM album_photos WHERE albumId=?",arrayOf(query.album)).use { rows -> check(rows.moveToFirst());rows.getInt(0) }
        CollectionPreview(count,entries)
    } }
    /** Existing local annotations only; this never starts recognition or contacts a map service. */
    fun special(kind: String): Flow<CollectionPreview> = changes().map { withContext(Dispatchers.IO) {
        require(kind=="People" || kind=="Places")
        val sql=if(kind=="People") "SELECT f.cropPath FROM faces f JOIN people p ON p.id=f.personId WHERE f.cropPath<>'' ORDER BY p.name COLLATE NOCASE,f.id LIMIT 4"
            else "SELECT thumbnailPath FROM photos WHERE archived=0 AND latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180 AND thumbnailPath<>'' ORDER BY COALESCE(capturedAt,importedAt) DESC,id LIMIT 4"
        val paths=database.openHelper.readableDatabase.query(sql).use { rows -> buildList { while(rows.moveToNext()) add(rows.getString(0)) } }
        CollectionPreview(-1,emptyList(),paths)
    } }
}
