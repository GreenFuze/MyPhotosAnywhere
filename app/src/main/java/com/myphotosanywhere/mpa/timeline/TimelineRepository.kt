package com.myphotosanywhere.mpa.timeline

import android.database.Cursor
import androidx.room.InvalidationTracker
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.flow.map
import java.io.File

/** All database and filesystem work stays off the UI thread and bounded by a page or cursor window. */
internal class TimelineRepository(internal val database: PhotoDatabase,
    private val originalExists: (String)->Boolean={ it.isNotBlank() && File(it).isFile }) {
    private val importedPeople=com.myphotosanywhere.mpa.people.ImportedPeopleCodec()
    suspend fun page(query: TimelineQuery,cursor: TimelineCursor?,direction: TimelineDirection,
        limit: Int,inclusive: Boolean=false): List<TimelineEntry> = withContext(Dispatchers.IO) {
        require(limit in 1..300)
        if(query.needsMetadata) return@withContext searchPage(query,cursor,direction,limit,inclusive)
        val result=ArrayList<TimelineEntry>(limit)
        var boundary=cursor
        var include=inclusive
        while(result.size<limit) {
            currentCoroutineContext().ensureActive()
            val batch=database.openHelper.readableDatabase.query(TimelineSql(query).page(boundary,direction,minOf(90,limit),include)).use { rows ->
                buildList { while(rows.moveToNext()) add(read(rows)) }
            }
            if(batch.isEmpty()) break
            for(entry in batch) {
                currentCoroutineContext().ensureActive()
                if(query.filter!="Offline" || originalExists(entry.originalPath)) result.add(entry)
                if(result.size==limit) break
            }
            if(result.size==limit || batch.size<minOf(90,limit)) break
            boundary=batch.last().cursor;include=false
        }
        if(direction==TimelineDirection.BEFORE) result.reverse()
        result
    }

    private suspend fun searchPage(query: TimelineQuery,cursor: TimelineCursor?,direction: TimelineDirection,limit: Int,inclusive: Boolean): List<TimelineEntry> {
        val result=ArrayList<TimelineEntry>(limit)
        database.openHelper.readableDatabase.query(TimelineSql(query).scan(cursor,direction,inclusive)).use { rows ->
            forwardWindow(rows)
            while(rows.moveToNext()) {
                currentCoroutineContext().ensureActive()
                val entry=read(rows)
                if(accepts(query,entry)) result.add(entry.copy(searchDocument=null))
                if(result.size==limit) break
            }
        }
        if(direction==TimelineDirection.BEFORE) result.reverse()
        return result
    }

    internal fun forwardWindow(rows: Cursor) {
        if(android.os.Build.VERSION.SDK_INT>=28 && rows is android.database.sqlite.SQLiteCursor) {
            rows.window=android.database.CursorWindow("MPA bounded metadata cursor",8L*1024*1024)
            rows.setFillWindowForwardOnly(true)
        }
    }

    suspend fun around(query: TimelineQuery,cursor: TimelineCursor?,limit: Int): List<TimelineEntry> {
        if(cursor==null) return page(query,null,TimelineDirection.AFTER,limit)
        val before=page(query,cursor,TimelineDirection.BEFORE,limit/2)
        val after=page(query,cursor,TimelineDirection.AFTER,limit-before.size,true)
        if(before.size+after.size==limit || before.isEmpty()) return before+after
        val extra=page(query,before.first().cursor,TimelineDirection.BEFORE,limit-before.size-after.size)
        return extra+before+after
    }

    suspend fun find(query: TimelineQuery,key: String): TimelineEntry?=withContext(Dispatchers.IO) {
        database.openHelper.readableDatabase.query(TimelineSql(query).identity(key)).use { rows ->
            if(rows.moveToFirst()) read(rows).takeIf { accepts(query,it) }?.copy(searchDocument=null) else null
        }
    }

    /** Follow only the existing verified cloud-to-local association when restoring a grid anchor. */
    suspend fun reanchor(query: TimelineQuery,cursor: TimelineCursor): TimelineCursor=withContext(Dispatchers.IO) {
        find(query,cursor.key)?.cursor ?: if(cursor.key.startsWith("cloud:")) {
            val cloud=database.cloud().photo(cursor.key.removePrefix("cloud:"))
            cloud?.photoId?.takeIf { !cloud.removed }?.let { find(query,"photo:$it")?.cursor } ?: cursor
        } else cursor
    }

    suspend fun seek(query: TimelineQuery,beforeExclusive: Long): TimelineCursor?=withContext(Dispatchers.IO) {
        val candidate=database.openHelper.readableDatabase.query(TimelineSql(query).date(beforeExclusive)).use { rows ->
            if(rows.moveToFirst()) read(rows) else null
        } ?: return@withContext null
        if(accepts(query,candidate)) candidate.cursor
        else {
            val dated=if(query.search!=null || query.importedPerson!=null) query.copy(untilMillis=minOf(query.untilMillis ?: beforeExclusive,beforeExclusive)) else query
            page(dated,candidate.cursor,TimelineDirection.AFTER,1).firstOrNull()?.cursor
        }
    }

    suspend fun resolve(entry: TimelineEntry): TimelineMedia=withContext(Dispatchers.IO) {
        database.withTransaction {
            val cloud=entry.cloudKey?.let { database.cloud().photo(it) }?.takeIf { current ->
                !current.removed && current.bytes==entry.bytes && when {
                    !entry.sha256.isNullOrBlank() && !current.sha256.isNullOrBlank() -> current.sha256==entry.sha256
                    entry.md5!=null && current.md5!=null -> current.md5==entry.md5
                    else -> current.version==entry.version
                }
            }
            val photo=(entry.photoId ?: cloud?.photoId)?.let { database.photos().find(it) }?.takeIf {
                it.bytes==entry.bytes && (entry.photoId==null || it.sha256==entry.sha256)
            }
            TimelineMedia(entry,photo,cloud)
        }
    }

    internal fun observe(onChanged: ()->Unit): AutoCloseable {
        val observer=object: InvalidationTracker.Observer("photos","cloud_photos","drive_sources","album_photos","faces","people","albums") {
            override fun onInvalidated(tables: Set<String>)=onChanged()
        }
        database.invalidationTracker.addObserver(observer)
        return AutoCloseable { database.invalidationTracker.removeObserver(observer) }
    }

    internal fun accepts(query: TimelineQuery,entry: TimelineEntry): Boolean {
        if(query.filter=="Offline" && !originalExists(entry.originalPath)) return false
        query.importedPerson?.let { label ->
            if(entry.photoId==null || !importedPeople.matches(label,entry.binding,requireNotNull(entry.searchDocument).metadata)) return false
        }
        val text=query.searchText ?: return true
        return text.matches(requireNotNull(entry.searchDocument) { "Search cursor lacks metadata" },java.time.ZoneId.of(query.searchZone))
    }

    private fun changes()=callbackFlow {
        val observation=observe { trySend(Unit) }
        trySend(Unit)
        awaitClose { observation.close() }
    }.conflate()

    fun media(entry: TimelineEntry)=changes().map { resolve(entry) }
    fun photo(id: String)=changes().map { withContext(Dispatchers.IO) { database.photos().find(id) } }

    suspend fun originalAvailable(media: TimelineMedia)=withContext(Dispatchers.IO) {
        media.photo?.originalPath?.let(originalExists) ?: false
    }

    internal fun read(row: Cursor)=TimelineEntry(
        row.getString(0),row.nullableString(1),row.nullableString(2),if(row.isNull(3)) null else row.getLong(3),
        TimestampProvenance.valueOf(row.getString(4)),row.getString(5),row.getString(6),row.getString(7),row.getString(8),
        row.getLong(9),row.getLong(10),row.nullableString(11),row.nullableString(12),row.nullableString(13),row.nullableString(14),row.nullableString(15),if(row.columnCount<=16) null else com.myphotosanywhere.mpa.search.SearchDocument(
            row.getString(5),row.getString(6),!row.isNull(1),if(row.isNull(18)) null else row.getLong(18),
            row.nullableString(16).orEmpty(),row.nullableString(17).orEmpty(),row.nullableString(19).orEmpty(),
            row.nullableString(24).orEmpty(),row.nullableString(25).orEmpty(),!row.isNull(20) && row.getInt(20)==1,!row.isNull(21),
            if(row.isNull(22)) null else row.getDouble(22),if(row.isNull(23)) null else row.getDouble(23)))

    private fun Cursor.nullableString(index: Int)=if(isNull(index)) null else getString(index)
}
