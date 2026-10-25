package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.job
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorControllerJournalTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val key = keys()
    private val publisher = keys()
    private val library = UUID.randomUUID()
    private val actor = UUID.randomUUID()
    private val publisherId = UUID.randomUUID()
    private val pin = LibraryConnectionDescriptor(library,actor,1,CatalogWorkspace("test-account","test-workspace"),key.public.encoded)
    private val signer = ProtocolDocumentSigner(key.private,key.public)
    private val codec = CanonicalProtocolJson()
    private class Capacity : StorageBudget.Capacity {
        var bytes = 1024L * 1024 * 1024
        override fun available(directory: File) = bytes
        override fun allocatable(directory: File) = bytes
        override fun allocate(directory: File,bytes: Long) { error("Controller history must never evict originals.") }
    }
    private fun store(capacity: Capacity = Capacity()) = ProcessorLibraryStateStore(context,StorageBudget(capacity))
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val reader = object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount(pin.workspace.accountId,"synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        }
        val connection = ProcessorLibraryConnection(store,reader)
        connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private fun policy(sequence: Long,revoked: Boolean = false,signing: ProtocolDocumentSigner = signer,minor: Int = 0): ByteArray {
        val publicKey = Base64.getEncoder().encodeToString(publisher.public.encoded)
        val publisherKey = ProtocolDocumentSigner(publisher.private,publisher.public).keyId
        val value = if (revoked) "null" else """{"actorId":"$publisherId","epoch":"1","publicKeySpki":"$publicKey","keyId":"$publisherKey"}"""
        return envelope(sequence,"library-publisher-authorization","library.publishers.v1",
            """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"publisher":$value}""",signing,minor)
    }
    private fun source(sequence: Long,revision: Long,account: String = "test-account"): ByteArray = envelope(sequence,
        "library-source-configuration","library.sources.v1",
        """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"configurationRevision":"$revision","accounts":["$account"],"sources":[]}""")
    private fun envelope(sequence: Long,kind: String,capability: String,payload: String,signing: ProtocolDocumentSigner = signer,minor: Int = 0): ByteArray {
        val body = codec.encode("""{"format":"mpa","major":1,"minor":$minor,"libraryId":"$library","actorId":"$actor","epoch":"1","sequence":"$sequence","kind":"$kind","requiredCapabilities":["$capability"],"payload":$payload}""".toByteArray(Charsets.UTF_8))
        return SignedProtocolDocuments(setOf(capability)).encode(body,signing.sign(body))
    }
    private fun sql(store: ProcessorLibraryStateStore,command: String) {
        SQLiteDatabase.openDatabase(File(store.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use { it.execSQL(command) }
    }

    @Test fun authorityPersistsRevocationAndOlderReplayCannotRewind() = runBlocking {
        var store = store()
        try {
            approve(store); var journal = ProcessorLibraryControllerJournal(store)
            assertNull(journal.current().publisher)
            val first = policy(1); val removed = policy(4,true)
            assertEquals(publisherId,journal.accept(first).publisher?.publisher?.actorId)
            assertNull(journal.accept(removed).publisher?.publisher)
            assertEquals(4L,journal.accept(first).publisher?.sequence)
            store.close(); store = store(); journal = ProcessorLibraryControllerJournal(store)
            assertEquals(4L,journal.current().publisher?.sequence); assertNull(journal.current().publisher?.publisher)
            assertEquals(pin.encode(),journal.current().connection.encode())
        } finally { store.close() }
    }

    @Test fun sequencesCannotChangeEvidenceOrControllerRoles() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store); val first = policy(2)
            journal.accept(first)
            for (conflict in listOf(policy(2,true),policy(2,minor=1),source(2,1))) {
                try { journal.accept(conflict); fail("Accepted counter reuse") } catch (_: ProcessorControllerConflict) { }
                assertEquals(2L,journal.current().publisher?.sequence); assertNull(journal.current().sources)
            }
            assertEquals(2L,journal.accept(first).publisher?.sequence)
        } finally { store.close() }
    }

    @Test fun sourceHistoryIsOrderIndependentAndCannotRegressRevision() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store)
            val later = source(9,3); val older = source(3,1)
            journal.accept(later); journal.accept(older); journal.accept(source(7,3)); journal.accept(source(5,2))
            assertEquals(9L,journal.current().sourceSequence); assertEquals(3L,journal.current().sources?.revision)
            for (conflict in listOf(source(8,4),source(4,3),source(10,2))) {
                try { journal.accept(conflict); fail("Accepted nonmonotonic source history") } catch (_: ProcessorControllerConflict) { }
                assertEquals(9L,journal.current().sourceSequence)
            }
        } finally { store.close() }
    }

    @Test fun ascendingSourcesAndEqualRevisionsRequireExactPayload() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store)
            journal.accept(source(1,1)); journal.accept(source(2,1)); journal.accept(source(3,2))
            assertEquals(3L,journal.current().sourceSequence)
            val changed = envelope(4,"library-source-configuration","library.sources.v1",
                """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"configurationRevision":"2","accounts":["another-account","test-account"],"sources":[]}""")
            try { journal.accept(changed); fail("Changed source payload reused a revision") } catch (_: ProcessorControllerConflict) { }
            assertEquals(listOf("test-account"),journal.current().sources?.accounts)
        } finally { store.close() }
    }

    @Test fun signatureScopeAndPayloadFailuresNeverInitializeHistory() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store)
            val wrong = keys(); val invalid = listOf(policy(1,signing=ProtocolDocumentSigner(wrong.private,wrong.public)),source(2,1,"wrong-account"),ByteArray(262145))
            for (bytes in invalid) {
                try { journal.accept(bytes); fail("Accepted invalid signed evidence") } catch (_: ProcessorProtocolException) { }
                assertNull(journal.current().publisher); assertNull(journal.current().sourceSequence)
            }
            assertEquals(3L,journal.accept(policy(3)).publisher?.sequence)
        } finally { store.close() }
    }

    @Test fun failedTransactionAndLowSpacePreserveLastAcceptedState() = runBlocking {
        val capacity = Capacity(); val store = store(capacity)
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store); val first = policy(1)
            journal.accept(first)
            sql(store,"CREATE TRIGGER reject_selection BEFORE UPDATE ON controller_selection BEGIN SELECT RAISE(ABORT,'synthetic failure'); END")
            try { journal.accept(policy(2,true)); fail("Acknowledged failed native commit") } catch (_: SQLiteException) { }
            assertEquals(1L,journal.current().publisher?.sequence)
            sql(store,"DROP TRIGGER reject_selection")
            capacity.bytes = 0
            assertEquals(1L,journal.accept(first).publisher?.sequence)
            try { journal.accept(policy(2,true)); fail("Wrote without safety margin") } catch (_: ProcessorClientStorageUnavailable) { }
            assertEquals(1L,journal.current().publisher?.sequence)
            capacity.bytes = 1024L * 1024 * 1024
            assertEquals(2L,journal.accept(policy(2,true)).publisher?.sequence)
        } finally { store.close() }
    }

    @Test fun damagedSelectedEvidenceAndRewoundPointerFailClosed() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store)
            journal.accept(policy(1)); journal.accept(policy(3,true))
            sql(store,"UPDATE controller_selection SET sequence=1")
            try { journal.current(); fail("Trusted rewound controller") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store,"UPDATE controller_selection SET sequence=3")
            sql(store,"UPDATE controller_document SET sha256='${"0".repeat(64)}' WHERE sequence=3")
            try { journal.accept(policy(4)); fail("Repaired damaged evidence silently") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close() }
    }

    @Test fun lostAnchorOrHistoryCannotRecreateAcceptedTrust() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store); journal.accept(policy(1))
            sql(store,"DELETE FROM controller_anchor")
            try { journal.current(); fail("Forgot accepted anchor") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store,"DELETE FROM controller_selection"); sql(store,"DELETE FROM controller_document")
            try { journal.current(); fail("Forgot all accepted evidence") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store,"DELETE FROM approved_pin"); sql(store,"DELETE FROM client_meta WHERE key='approved_pin_sha256'")
            try { store.registration(); fail("Reenrolled over accepted history marker") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close() }
    }

    @Test fun concurrentOwnersHaveOneImmutableSequenceWinner() = runBlocking {
        val left = store(); val right = store()
        try {
            approve(left)
            val outcomes = supervisorScope { listOf(left to policy(1),right to policy(1,true)).map { (store,bytes) -> async {
                try { ProcessorLibraryControllerJournal(store).accept(bytes); true } catch (_: ProcessorControllerConflict) { false }
            } }.awaitAll() }
            assertEquals(1,outcomes.count { it })
            assertEquals(ProcessorLibraryControllerJournal(left).current().publisher?.publisherKeyId,
                ProcessorLibraryControllerJournal(right).current().publisher?.publisherKeyId)
        } finally { left.close(); right.close() }
    }

    @Test fun schemaTwoMigrationPreservesAccountAndPin() = runBlocking {
        var store = store(); val path = File(store.directory,"connection.sqlite")
        try {
            approve(store); store.close()
            SQLiteDatabase.openDatabase(path.absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use { database ->
                database.execSQL("DROP TABLE workspace_event");database.execSQL("DROP TABLE workspace_read"); database.execSQL("DROP TABLE workspace_candidate"); database.execSQL("DROP TABLE workspace_attempt"); database.execSQL("DROP TABLE controller_selection"); database.execSQL("DROP TABLE controller_document"); database.execSQL("DROP TABLE controller_anchor")
                database.version = 2
            }
            store = store(); assertEquals(pin.encode(),store.current()?.encode())
            assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            assertNull(ProcessorLibraryControllerJournal(store).current().publisher)
            assertEquals(1L,ProcessorLibraryControllerJournal(store).accept(policy(1)).publisher?.sequence)
            SQLiteDatabase.openDatabase(path.absolutePath,null,SQLiteDatabase.OPEN_READONLY).use { assertEquals(4,it.version) }
        } finally { store.close() }
    }

    @Test fun migrationWithoutSpaceLeavesOldSchemaIntact() = runBlocking {
        val capacity = Capacity(); var store = store(capacity); val path = File(store.directory,"connection.sqlite")
        try {
            approve(store); store.close()
            SQLiteDatabase.openDatabase(path.absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use { database ->
                database.execSQL("DROP TABLE workspace_event");database.execSQL("DROP TABLE workspace_read"); database.execSQL("DROP TABLE workspace_candidate"); database.execSQL("DROP TABLE workspace_attempt"); database.execSQL("DROP TABLE controller_selection"); database.execSQL("DROP TABLE controller_document"); database.execSQL("DROP TABLE controller_anchor"); database.version = 2
            }
            capacity.bytes = 0; store = store(capacity)
            try { store.registration(); fail("Migrated without capacity") } catch (_: ProcessorClientStorageUnavailable) { }
            store.close()
            SQLiteDatabase.openDatabase(path.absolutePath,null,SQLiteDatabase.OPEN_READONLY).use { database ->
                assertEquals(2,database.version)
                database.rawQuery("SELECT code FROM approved_pin",null).use { assertTrue(it.moveToFirst()); assertEquals(pin.encode(),it.getString(0)) }
                database.rawQuery("SELECT email FROM credential_selector",null).use { assertTrue(it.moveToFirst()); assertEquals("synthetic@example.invalid",it.getString(0)) }
            }
            capacity.bytes = 1024L * 1024 * 1024; store = store(capacity)
            assertEquals(pin.encode(),store.current()?.encode())
        } finally { store.close() }
    }

    @Test fun canceledTransactionRetainsItsAcceptedAnchor() = runBlocking {
        val store = store()
        try {
            approve(store); val journal = ProcessorLibraryControllerJournal(store); journal.accept(policy(1))
            supervisorScope {
                val canceled = async {
                    val job = currentCoroutineContext().job
                    store.controllerTransaction { database,_ -> database.execSQL("DELETE FROM controller_anchor"); job.cancel() }
                }
                try { canceled.await(); fail("Canceled native transaction completed") } catch (_: CancellationException) { }
            }
            assertEquals(1L,journal.current().publisher?.sequence)
        } finally { store.close() }
    }

    @Test fun missingAccountRequiresFreshApprovalBeforeReception() = runBlocking {
        val store = store()
        try {
            approve(store); sql(store,"DELETE FROM credential_selector")
            val journal = ProcessorLibraryControllerJournal(store); assertNull(journal.current().publisher)
            try { journal.accept(policy(1)); fail("Missing credential selector authorized reception") } catch (_: java.io.IOException) { }
            assertNull(journal.current().publisher)
            approve(store); assertEquals(1L,journal.accept(policy(1)).publisher?.sequence)
        } finally { store.close() }
    }

    private fun keys() = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
}
