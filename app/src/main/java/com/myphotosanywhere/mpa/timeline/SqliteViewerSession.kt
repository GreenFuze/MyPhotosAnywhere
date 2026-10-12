package com.myphotosanywhere.mpa.timeline

import android.database.sqlite.SQLiteDatabase
import androidx.room.withTransaction
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import java.io.File
import java.io.IOException
import java.util.UUID

internal class ViewerPreparationException(message: String): IOException(message)

/** Disposable sequence indices live outside backed-up and reclaimable cache storage. */
internal class ViewerSessionStore(private val directory: File,private val repository: TimelineRepository,
    private val storage: com.myphotosanywhere.mpa.data.StorageBudget?=null) {
    private var initialized=false
    private val initialization=Any()
    private val disposal=kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob()+Dispatchers.IO)

    /** Disposal belongs to the application, so clearing a screen cannot cancel lease cleanup. */
    fun dispose(session: ViewerSession) {
        disposal.launch { session.close() }
    }

    suspend fun prepare(query: TimelineQuery,selected: TimelineEntry): ViewerSession=withContext(Dispatchers.IO) {
        synchronized(initialization) {
            if(!initialized) {
                check(directory.isDirectory || directory.mkdirs()) { "Cannot create private viewer session directory" }
                // A new process never reuses a previous process's possibly incomplete sequence.
                directory.listFiles()?.filter { it.name.startsWith("viewer-") && it.isFile }?.forEach {
                    check(it.delete()) { "Cannot remove stale viewer sequence" }
                }
                initialized=true
            }
        }
        val id=UUID.randomUUID().toString()
        val file=File(directory,"viewer-$id.db")
        // SQLite's native path parser also runs in host tests. Forward separators are
        // accepted on both Android and Windows, unlike Windows File.toString paths.
        val index=try { SQLiteDatabase.openOrCreateDatabase(file.absolutePath.replace('\\','/'),null) }
        catch(error: android.database.sqlite.SQLiteException) { deleteIndex(file);throw error }
        var complete=false
        try {
            requireCapacity()
            var capacityCheckAt=4L*1024*1024
            // This disposable index is never recovered after a crash; persisted media is untouched.
            index.execSQL("PRAGMA synchronous=OFF")
            // Only this disposable index uses an in-memory rollback journal.
            // Avoid opening/deleting a disk journal for every bounded write batch.
            index.rawQuery("PRAGMA journal_mode=MEMORY",null).use { check(it.moveToFirst() && it.getString(0).equals("memory",true)) }
            index.execSQL("""CREATE TABLE entries(ordinal INTEGER PRIMARY KEY,canonicalKey TEXT NOT NULL UNIQUE,
                photoId TEXT,cloudKey TEXT,effectiveMillis INTEGER,timestampSource TEXT NOT NULL,
                displayName TEXT NOT NULL,mimeType TEXT NOT NULL,originalPath TEXT NOT NULL,thumbnailPath TEXT NOT NULL,
                thumbnailUpdatedAt INTEGER NOT NULL,bytes INTEGER NOT NULL,sha256 TEXT,md5 TEXT,version TEXT,bindingKey TEXT,remoteId TEXT)""")
            var count=0
            var initial=-1
            repository.database.withTransaction {
                // Capture a verified representation transition before reading the same source snapshot.
                val currentCloud=selected.cloudKey?.let { repository.database.cloud().photo(it) }
                val replacement=if(selected.photoId==null && currentCloud?.photoId!=null &&
                    currentCloud.bytes==selected.bytes && currentCloud.md5==selected.md5 && currentCloud.version==selected.version)
                    "photo:${currentCloud.photoId}" else selected.key
                repository.database.openHelper.readableDatabase.query(TimelineSql(query).ordered()).use { cursor ->
                    index.compileStatement("INSERT INTO entries VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)").use { statement ->
                        var transaction=false
                        try {
                            while(cursor.moveToNext()) {
                                currentCoroutineContext().ensureActive()
                                val entry=repository.read(cursor)
                                if(!repository.accepts(query,entry)) continue
                                if(!transaction) { index.beginTransaction();transaction=true }
                                statement.clearBindings();statement.bindLong(1,count.toLong())
                                for(column in 0..15) {
                                    val binding=column+2
                                    when {
                                        cursor.isNull(column) -> statement.bindNull(binding)
                                        column==3 || column==9 || column==10 -> statement.bindLong(binding,cursor.getLong(column))
                                        else -> statement.bindString(binding,cursor.getString(column))
                                    }
                                }
                                statement.executeInsert()
                                if(entry.key==selected.key || entry.key==replacement) initial=count
                                count++
                                if(count%60==0) {
                                    index.setTransactionSuccessful();index.endTransaction();transaction=false
                                    if(file.length()>=capacityCheckAt) {
                                        requireCapacity();capacityCheckAt=file.length()+4L*1024*1024
                                    }
                                }
                            }
                            if(transaction) index.setTransactionSuccessful()
                        } finally { if(transaction) index.endTransaction() }
                    }
                }
            }
            currentCoroutineContext().ensureActive()
            if(initial<0) throw ViewerPreparationException("This photo is no longer in the selected view. Its preview remains available; return to the gallery or retry.")
            check(count>0)
            complete=true
            SqliteViewerSession(id,file,index,repository,count,initial)
        } finally {
            if(!complete) { index.close();deleteIndex(file) }
        }
    }

    private fun requireCapacity() {
        try { storage?.requireBytes(directory,20L*1024*1024,"Not enough device space to prepare photo browsing. Free space and retry; the preview remains available.") }
        catch(error: IllegalArgumentException) { throw ViewerPreparationException(requireNotNull(error.message)) }
    }

    internal companion object {
        fun deleteIndex(file: File) {
            for(suffix in listOf("","-journal","-wal","-shm")) {
                val artifact=File(file.path+suffix)
                check(!artifact.exists() || artifact.delete()) { "Cannot remove disposable viewer index" }
            }
        }
    }
}

private class SqliteViewerSession(override val id: String,private val file: File,
    private val index: SQLiteDatabase,private val repository: TimelineRepository,
    override val count: Int,override val initialPage: Int): ViewerSession {
    private val lock=Any()
    private var closed=false

    override suspend fun entry(page: Int): TimelineEntry=withContext(Dispatchers.IO) {
        require(page in 0 until count)
        synchronized(lock) {
            if(closed) throw kotlinx.coroutines.CancellationException("Viewer session has been disposed")
            index.rawQuery("""SELECT canonicalKey,photoId,cloudKey,effectiveMillis,timestampSource,displayName,mimeType,
                originalPath,thumbnailPath,thumbnailUpdatedAt,bytes,sha256,md5,version,bindingKey,remoteId
                FROM entries WHERE ordinal=?""",arrayOf(page.toString())).use { cursor ->
                check(cursor.moveToFirst()) { "Viewer sequence is missing an ordinal" }
                repository.read(cursor)
            }
        }
    }

    override fun close()=synchronized(lock) {
        if(!closed) { closed=true;index.close();ViewerSessionStore.deleteIndex(file) }
    }
}
