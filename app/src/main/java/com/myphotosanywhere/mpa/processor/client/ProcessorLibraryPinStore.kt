package com.myphotosanywhere.mpa.processor.client

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

internal class ProcessorLibraryPinConflict : IOException("This device already trusts a different library connection. Its accepted state was preserved.")

/** Serial owner of an independently approved public pin, excluded from backup/transfer.
 * No credentials, writable source access, accepted policy or catalog activation lives here.
 * Every call joins its own SQLite operation. Close waits for admitted operations and then
 * fences later callers; the borrowing client must join its network callers before close. */
internal class ProcessorLibraryPinStore(context: Context, private val storage: StorageBudget, stateDirectory: File? = null) {
    private val application = context.applicationContext
    internal val directory = stateDirectory ?: File(application.noBackupFilesDir, "processor-client")
    init {
        require(directory.canonicalPath.startsWith(application.noBackupFilesDir.canonicalPath + File.separator)) {
            "Processor client trust state must stay inside private, nontransferable storage."
        }
    }
    private val mutex = Mutex()
    private var helper: PinDatabase? = null
    private var closed = false

    suspend fun current(): LibraryConnectionDescriptor? = access { database ->
        database.beginTransactionNonExclusive()
        try { read(database).also { database.setTransactionSuccessful() } }
        finally { database.endTransaction() }
    }

    internal suspend fun approve(review: ProcessorLibraryConnectionReview): LibraryConnectionDescriptor = access { database ->
        database.beginTransaction()
        try {
            val current = read(database)
            if (current != null) {
                if (current.encode() != review.descriptor.encode()) throw ProcessorLibraryPinConflict()
                database.setTransactionSuccessful()
                current
            } else {
                storage.requireBytes(directory, WRITE_RESERVE, "Free some device space before approving this library. Existing photos were kept.")
                currentCoroutineContext().ensureActive()
                database.execSQL("INSERT INTO approved_pin(singleton,code,document_sha256) VALUES(1,?,?)",
                    arrayOf(review.descriptor.encode(), review.descriptor.documentSha256))
                database.execSQL("INSERT INTO client_meta(key,value) VALUES('approved_pin_sha256',?)", arrayOf(review.descriptor.documentSha256))
                currentCoroutineContext().ensureActive()
                database.setTransactionSuccessful()
                review.descriptor
            }
        } finally { database.endTransaction() }
    }

    suspend fun close() = withContext(NonCancellable + Dispatchers.IO) {
        mutex.withLock {
            if (!closed) { helper?.close(); helper = null; closed = true }
        }
    }

    private suspend fun <T> access(action: suspend (SQLiteDatabase) -> T): T = withContext(Dispatchers.IO) {
        mutex.withLock {
            check(!closed) { "Processor client pin owner is closed." }
            currentCoroutineContext().ensureActive()
            val owner = helper ?: run {
                check(directory.isDirectory || directory.mkdirs()) { "Processor client state directory is unavailable." }
                val path = File(directory, "connection.sqlite")
                if (!path.exists()) storage.requireBytes(directory, WRITE_RESERVE, "Free some device space before opening library state. Existing photos were kept.")
                PinDatabase(application, path.absolutePath).also { helper = it }
            }
            action(owner.writableDatabase)
        }
    }

    private fun read(database: SQLiteDatabase): LibraryConnectionDescriptor? {
        val marker = database.rawQuery("SELECT length(value),value FROM client_meta WHERE key='approved_pin_sha256'", null).use { row ->
            if (!row.moveToFirst()) null else {
                check(row.getLong(0) == 64L) { "Approved pin marker is corrupt." }
                row.getString(1)
            }
        }
        val encoded = database.rawQuery("SELECT length(code),length(document_sha256) FROM approved_pin WHERE singleton=1", null).use { row ->
            if (!row.moveToFirst()) {
                check(marker == null) { "Accepted library pin is missing; never recreate trust over accepted state." }
                return null
            }
            check(row.getLong(0) in 1L..LibraryConnectionDescriptor.MAX_CODE_CHARACTERS.toLong() && row.getLong(1) == 64L) { "Approved library pin exceeds its bounds." }
            database.rawQuery("SELECT code,document_sha256 FROM approved_pin WHERE singleton=1", null).use { content ->
                check(content.moveToFirst()) { "Approved library pin changed during its owned read." }
                content.getString(0) to content.getString(1)
            }
        }
        val descriptor = LibraryConnectionDescriptor.decode(encoded.first)
        check(descriptor.documentSha256 == encoded.second && marker == encoded.second) { "Accepted library pin disagrees with its approval marker." }
        return descriptor
    }

    private class PinDatabase(context: Context, path: String) : SQLiteOpenHelper(context, path, null, 1) {
        override fun onCreate(database: SQLiteDatabase) {
            database.execSQL("CREATE TABLE approved_pin(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 4096),document_sha256 TEXT NOT NULL CHECK(length(document_sha256)=64))")
            database.execSQL("CREATE TABLE client_meta(key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL)")
        }
        override fun onUpgrade(database: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
            error("Unsupported processor client pin schema upgrade: $oldVersion to $newVersion. Accepted state was preserved.")
        }
    }
    private companion object { const val WRITE_RESERVE = 64L * 1024 * 1024 }
}
