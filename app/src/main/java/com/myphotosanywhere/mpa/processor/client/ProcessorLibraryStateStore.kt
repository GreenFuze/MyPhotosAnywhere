package com.myphotosanywhere.mpa.processor.client

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import androidx.core.content.contentValuesOf
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
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
internal class ProcessorClientStorageUnavailable(cause: Exception? = null) : IOException("Free some device space before saving this library connection. Existing photos and accepted state were kept.",cause)
internal class ProcessorLibraryStateCorrupt(message: String, cause: Exception? = null) : IllegalStateException(message,cause)
internal data class ProcessorLibraryRegistration(val descriptor: LibraryConnectionDescriptor, val account: ProcessorLibraryAccount?)

/** Serialized nontransferable public trust, credential selector and signed-history owner. A selector is
 * an email, NEVER a token or authority: every Google connection still verifies permission
 * ID. Each operation joins SQLite work; close joins even from canceled callers. */
internal class ProcessorLibraryStateStore(context: Context, private val storage: StorageBudget, stateDirectory: File? = null) {
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

    suspend fun current(): LibraryConnectionDescriptor? = registration()?.descriptor
    suspend fun registration(): ProcessorLibraryRegistration? = access { database ->
        database.beginTransactionNonExclusive()
        try { readRegistration(database).also { database.setTransactionSuccessful() } }
        finally { database.endTransaction() }
    }

    internal suspend fun approve(review: ProcessorLibraryConnectionReview): LibraryConnectionDescriptor = access { database ->
        database.beginTransaction()
        try {
            val current = readRegistration(database)
            if (current != null && current.descriptor.encode() != review.descriptor.encode()) throw ProcessorLibraryPinConflict()
            check(review.account.id == review.descriptor.workspace.accountId) { "Reviewed account and approved workspace disagree." }
            if (current == null || current.account != review.account) {
                requireWriteBudget()
                currentCoroutineContext().ensureActive()
                if (current == null) {
                    database.execSQL("INSERT INTO approved_pin(singleton,code,document_sha256) VALUES(1,?,?)",
                        arrayOf(review.descriptor.encode(), review.descriptor.documentSha256))
                    database.execSQL("INSERT INTO client_meta(key,value) VALUES('approved_pin_sha256',?)", arrayOf(review.descriptor.documentSha256))
                }
                val selector = contentValuesOf("account_id" to review.account.id,"email" to review.account.email)
                if (database.update("credential_selector",selector,"singleton=1",null) == 0) {
                    selector.put("singleton",1)
                    database.insertOrThrow("credential_selector",null,selector)
                }
                currentCoroutineContext().ensureActive()
            }
            database.setTransactionSuccessful()
            current?.descriptor ?: review.descriptor
        } finally { database.endTransaction() }
    }

    suspend fun close() = withContext(NonCancellable + Dispatchers.IO) {
        mutex.withLock { if (!closed) { helper?.close(); helper = null; closed = true } }
    }

    internal fun requireWriteBudget(extraBytes: Long = 0) {
        require(extraBytes in 0L..(3L * 256 * 1024 + 65536))
        try { storage.requireBytes(directory, Math.addExact(WRITE_RESERVE,extraBytes), "Insufficient processor client state capacity.") }
        catch (error: IllegalArgumentException) { throw ProcessorClientStorageUnavailable(error) }
    }
    private suspend fun <T> access(action: suspend (SQLiteDatabase) -> T): T = withContext(Dispatchers.IO) {
        mutex.withLock {
            check(!closed) { "Processor client pin owner is closed." }
            currentCoroutineContext().ensureActive()
            val owner = helper ?: run {
                if (!directory.isDirectory && !directory.mkdirs()) throw ProcessorClientStorageUnavailable()
                val path = File(directory,"connection.sqlite")
                if (!path.exists()) requireWriteBudget()
                PinDatabase(application,path.absolutePath) { requireWriteBudget() }.also { helper = it }
            }
            action(owner.writableDatabase)
        }
    }

    private fun readRegistration(database: SQLiteDatabase): ProcessorLibraryRegistration? {
        val pin = readPin(database)
        val account = database.rawQuery("SELECT length(account_id),length(email) FROM credential_selector WHERE singleton=1",null).use { row ->
            if (!row.moveToFirst()) null else {
                state(row.getLong(0) in 1L..256L && row.getLong(1) in 1L..320L,"Saved Google account exceeds its bounds.")
                database.rawQuery("SELECT account_id,email FROM credential_selector WHERE singleton=1",null).use { content ->
                    state(content.moveToFirst(),"Saved Google account changed during its owned read.")
                    try { ProcessorLibraryAccount(content.getString(0),content.getString(1)) }
                    catch (error: IOException) { throw ProcessorLibraryStateCorrupt("Saved Google account cannot be decoded.",error) }
                }
            }
        }
        state(account == null || pin != null && account.id == pin.workspace.accountId,"Saved Google account disagrees with accepted library trust.")
        return pin?.let { ProcessorLibraryRegistration(it,account) }
    }
    internal suspend fun <T> controllerTransaction(action: (SQLiteDatabase,ProcessorLibraryRegistration)->T): T = access { database ->
        database.beginTransaction()
        try {
            val registration = readRegistration(database) ?: throw IOException("Approve this device's library connection before reading shared policies.")
            val result = action(database,registration)
            currentCoroutineContext().ensureActive()
            database.setTransactionSuccessful()
            result
        } finally { database.endTransaction() }
    }
    private fun hasControllerState(database: SQLiteDatabase): Boolean =
        database.rawQuery("SELECT 1 FROM controller_anchor UNION ALL SELECT 1 FROM controller_document UNION ALL SELECT 1 FROM controller_selection UNION ALL SELECT 1 FROM client_meta WHERE key='controller_anchor_sha256' LIMIT 1",null).use { it.moveToFirst() }

    private fun readPin(database: SQLiteDatabase): LibraryConnectionDescriptor? {
        val marker = database.rawQuery("SELECT length(value),value FROM client_meta WHERE key='approved_pin_sha256'",null).use { row ->
            if (!row.moveToFirst()) null else { state(row.getLong(0) == 64L,"Approved pin marker is corrupt."); row.getString(1) }
        }
        val encoded = database.rawQuery("SELECT length(code),length(document_sha256) FROM approved_pin WHERE singleton=1",null).use { row ->
            if (!row.moveToFirst()) { state(marker == null && !hasControllerState(database),"Accepted library pin is missing; never recreate trust over accepted state."); return null }
            state(row.getLong(0) in 1L..LibraryConnectionDescriptor.MAX_CODE_CHARACTERS.toLong() && row.getLong(1) == 64L,"Approved library pin exceeds its bounds.")
            database.rawQuery("SELECT code,document_sha256 FROM approved_pin WHERE singleton=1",null).use { content ->
                state(content.moveToFirst(),"Approved library pin changed during its owned read."); content.getString(0) to content.getString(1)
            }
        }
        val descriptor = try { LibraryConnectionDescriptor.decode(encoded.first) }
            catch (error: ProcessorProtocolException) { throw ProcessorLibraryStateCorrupt("Saved library pin cannot be decoded.",error) }
        state(descriptor.documentSha256 == encoded.second && marker == encoded.second,"Accepted library pin disagrees with its approval marker.")
        return descriptor
    }
    private fun state(valid: Boolean, message: String) { if (!valid) throw ProcessorLibraryStateCorrupt(message) }
    private class PinDatabase(context: Context, path: String, private val requireMigrationBudget: () -> Unit) : SQLiteOpenHelper(context,path,null,3) {
        override fun onCreate(database: SQLiteDatabase) {
            database.execSQL("CREATE TABLE approved_pin(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 4096),document_sha256 TEXT NOT NULL CHECK(length(document_sha256)=64))")
            database.execSQL("CREATE TABLE client_meta(key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL)")
            createSelector(database)
            createControllerJournal(database)
        }
        override fun onConfigure(database: SQLiteDatabase) { database.setForeignKeyConstraintsEnabled(true) }
        override fun onUpgrade(database: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
            if (oldVersion !in 1..2 || newVersion != 3)
                error("Unsupported processor client state upgrade: $oldVersion to $newVersion. Accepted state was preserved.")
            requireMigrationBudget()
            if (oldVersion == 1) createSelector(database)
            createControllerJournal(database)
        }
        private fun createControllerJournal(database: SQLiteDatabase) {
            database.execSQL("CREATE TABLE controller_anchor(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),connection_sha256 TEXT NOT NULL CHECK(length(connection_sha256)=64))")
            database.execSQL("CREATE TABLE controller_document(sequence INTEGER NOT NULL PRIMARY KEY CHECK(sequence>0),kind TEXT NOT NULL CHECK(kind IN ('library-publisher-authorization','library-source-configuration')),envelope BLOB NOT NULL CHECK(length(envelope) BETWEEN 1 AND 262144),sha256 TEXT NOT NULL CHECK(length(sha256)=64))")
            database.execSQL("CREATE INDEX controller_document_kind_sequence ON controller_document(kind,sequence)")
            database.execSQL("CREATE TABLE controller_selection(kind TEXT NOT NULL PRIMARY KEY CHECK(kind IN ('library-publisher-authorization','library-source-configuration')),sequence INTEGER NOT NULL REFERENCES controller_document(sequence) ON DELETE RESTRICT)")
        }
        private fun createSelector(database: SQLiteDatabase) {
            database.execSQL("CREATE TABLE credential_selector(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),account_id TEXT NOT NULL CHECK(length(account_id) BETWEEN 1 AND 256),email TEXT NOT NULL CHECK(length(email) BETWEEN 1 AND 320))")
        }
    }
    private companion object { const val WRITE_RESERVE = 64L * 1024 * 1024 }
}
