package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorControllerJournalDeviceTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("test-account","test-workspace"),key.public.encoded)
    private val signer = ProtocolDocumentSigner(key.private,key.public)
    private fun envelope(sequence: Long,source: Boolean = false): ByteArray {
        val kind = if (source) "library-source-configuration" else "library-publisher-authorization"
        val capability = if (source) "library.sources.v1" else "library.publishers.v1"
        val payload = if (source) """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"configurationRevision":"1","accounts":["test-account"],"sources":[]}"""
            else """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"publisher":null}"""
        val body = CanonicalProtocolJson().encode("""{"format":"mpa","major":1,"minor":0,"libraryId":"${pin.libraryId}","actorId":"${pin.controllerId}","epoch":"1","sequence":"$sequence","kind":"$kind","requiredCapabilities":["$capability"],"payload":$payload}""".toByteArray(Charsets.UTF_8))
        return SignedProtocolDocuments(setOf(capability)).encode(body,signer.sign(body))
    }
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val reader = object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount("test-account","synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        }
        val client = ProcessorLibraryConnection(store,reader); client.approve(client.review(pin.encode()),pin.documentSha256)
    }
    private fun store(directory: File) = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
    private fun sql(directory: File,action: (SQLiteDatabase) -> Unit) =
        SQLiteDatabase.openDatabase(File(directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private fun cleanup(directory: File) {
        check(directory.parentFile?.canonicalFile == context.noBackupFilesDir.canonicalFile && directory.name.startsWith("controller-test-"))
        check(!directory.exists() || directory.deleteRecursively())
    }

    @Test fun realSqlitePreservesRevocationAndRejectsAtomicCounterConflicts() = runBlocking<Unit> {
        val directory = File(context.noBackupFilesDir,"controller-test-${UUID.randomUUID()}"); var store = store(directory)
        try {
            approve(store); var journal = ProcessorLibraryControllerJournal(store)
            val old = envelope(1); journal.accept(old); journal.accept(envelope(4)); journal.accept(envelope(3,true))
            assertEquals(4L,journal.accept(old).publisher?.sequence)
            assertEquals(3L,journal.current().sourceSequence)
            try { journal.accept(envelope(4,true)); fail("Native SQLite accepted a controller counter collision") } catch (_: ProcessorControllerConflict) { }
            sql(directory) { it.execSQL("CREATE TRIGGER reject_current BEFORE UPDATE ON controller_selection BEGIN SELECT RAISE(ABORT,'synthetic failure'); END") }
            try { journal.accept(envelope(5)); fail("Failed native transaction was acknowledged") } catch (_: SQLiteException) { }
            store.close(); store = store(directory); journal = ProcessorLibraryControllerJournal(store)
            assertEquals(4L,journal.current().publisher?.sequence); assertNull(journal.current().publisher?.publisher)
            assertEquals(3L,journal.current().sourceSequence); assertEquals(pin.encode(),store.current()?.encode())
            sql(directory) { it.execSQL("DROP TRIGGER reject_current"); it.execSQL("UPDATE controller_selection SET sequence=1 WHERE kind='library-publisher-authorization'") }
            try { journal.current(); fail("Native SQLite accepted a rewound pointer") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close(); cleanup(directory) }
    }

    @Test fun nativeMigrationKeepsApprovedAccountAndOriginalBytes() = runBlocking<Unit> {
        val directory = File(context.noBackupFilesDir,"controller-test-${UUID.randomUUID()}"); var store = store(directory)
        try {
            approve(store); val original = File(directory,"original.jpg").apply { writeBytes(byteArrayOf(8,6,4,2)) }
            store.close()
            sql(directory) { database ->
                database.execSQL("DROP TABLE workspace_history");database.execSQL("DROP TABLE workspace_replay");database.execSQL("DROP TABLE workspace_event");database.execSQL("DROP TABLE workspace_read"); database.execSQL("DROP TABLE workspace_candidate"); database.execSQL("DROP TABLE workspace_attempt"); database.execSQL("DROP TABLE controller_selection"); database.execSQL("DROP TABLE controller_document"); database.execSQL("DROP TABLE controller_anchor"); database.version = 2
            }
            store = store(directory)
            assertEquals(pin.encode(),store.current()?.encode()); assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            val state = ProcessorLibraryControllerJournal(store).accept(envelope(1,true))
            assertEquals(1L,state.sourceSequence); assertEquals(1L,state.sources?.revision)
            assertArrayEquals(byteArrayOf(8,6,4,2),original.readBytes())
            sql(directory) { assertEquals(5,it.version) }
        } finally { store.close(); cleanup(directory) }
    }
    @Test fun nativeBorrowedPolicySnapshotSharesAtomicRollbackAndExactEnvelope() = runBlocking<Unit> {
        val directory = File(context.noBackupFilesDir,"controller-test-${UUID.randomUUID()}");var store = store(directory)
        try {
            approve(store);var journal = ProcessorLibraryControllerJournal(store);val source = envelope(2,true)
            journal.accept(envelope(1));journal.accept(source)
            val digest = java.security.MessageDigest.getInstance("SHA-256").digest(source).joinToString("") { "%02x".format(it.toInt() and 255) }
            store.controllerTransaction { database,registration ->
                val snapshot = ProcessorLibraryControllerJournal.currentIn(database,registration)
                assertEquals(2L,snapshot.sourceSequence);assertEquals(digest,snapshot.sourceEnvelopeSha256);assertNull(snapshot.publisher?.publisher)
            }
            try {
                store.controllerTransaction { database,registration ->
                    database.execSQL("INSERT INTO client_meta(key,value) VALUES('synthetic_staging','pending')")
                    database.execSQL("DELETE FROM controller_selection WHERE kind='library-source-configuration'")
                    ProcessorLibraryControllerJournal.currentIn(database,registration)
                };fail("Native caller acknowledged a damaged selected source")
            } catch (_: ProcessorLibraryStateCorrupt) { }
            store.close();store = store(directory);journal = ProcessorLibraryControllerJournal(store)
            assertEquals(digest,journal.current().sourceEnvelopeSha256)
            sql(directory) { database -> database.rawQuery("SELECT 1 FROM client_meta WHERE key='synthetic_staging'",null).use { assertFalse(it.moveToFirst()) } }
        } finally { store.close();cleanup(directory) }
    }

}
