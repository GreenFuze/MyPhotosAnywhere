package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner
import com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments
import java.io.File
import java.io.IOException
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withContext
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorWorkspaceInventoryTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("test-account","test-workspace"),key.public.encoded)
    private class Capacity : StorageBudget.Capacity {
        var bytes = 1024L * 1024 * 1024
        override fun available(directory: File) = bytes
        override fun allocatable(directory: File) = bytes
        override fun allocate(directory: File,bytes: Long) { error("Observation must never evict originals.") }
    }
    private fun store(capacity: Capacity = Capacity()) = ProcessorLibraryStateStore(context,StorageBudget(capacity))
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val reader = object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount("test-account","synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        }
        val connection = ProcessorLibraryConnection(store,reader);connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private fun metadata(id: String,version: Long = 1,mime: String = "application/json") = DriveWorkspaceMetadata(id,mime,version,2,listOf("test-workspace"),false,null,true)
    private fun candidate(meta: DriveWorkspaceMetadata) = ProcessorWorkspaceCandidate(meta.id,meta.version,meta.bytes,meta.mimeType)
    private fun sql(store: ProcessorLibraryStateStore,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(store.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private inner class Source(val observations: ProcessorWorkspaceObservations) : ProcessorWorkspaceInventorySource {
        override var connectionSha256 = pin.documentSha256
        var cursorValue = "cursor-first"
        var files = listOf(metadata("a"),metadata("b"))
        var pageSize = 1
        var sourceCalls = 0;var bodyReads = 0
        var beforeRead: (suspend (DriveWorkspaceMetadata)->Unit)? = null
        override suspend fun cursor(): DriveWorkspaceCursor {
            sourceCalls++;val attempt = checkNotNull(observations.lastAttempt())
            assertNull(attempt.inventoryStart);assertFalse(attempt.inventoryListed)
            return DriveWorkspaceCursor(pin.workspace.accountId,null,cursorValue)
        }
        override suspend fun page(token: String?): DriveWorkspacePage {
            sourceCalls++;val offset = token?.toInt() ?: 0;val end = minOf(offset + pageSize,files.size)
            return DriveWorkspacePage(files.subList(offset,end),if (end < files.size) end.toString() else null,false)
        }
        override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray {
            bodyReads++;assertTrue(checkNotNull(observations.lastAttempt()).inventoryListed)
            beforeRead?.invoke(expected)
            return "{}".toByteArray()
        }
    }
    private suspend fun rejects(action: suspend ()->Unit) { try { action();fail("Unverified or stale observation was accepted") } catch (_: IOException) { } }

    @Test fun candidatesSurviveFailedVerificationAndReopen() = runBlocking {
        var store = store()
        try {
            approve(store);var observations = ProcessorWorkspaceObservations(store);val source = Source(observations)
            source.beforeRead = {
                sql(store) { database -> database.rawQuery("SELECT count(*) FROM workspace_candidate",null).use { row -> assertTrue(row.moveToFirst());assertEquals(2,row.getInt(0)) } }
            }
            rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> throw IOException("Synthetic invalid signature") } }
            val failed = checkNotNull(observations.lastAttempt());assertEquals("cursor-first",failed.firstUnresolved)
            assertTrue(failed.inventoryListed);assertEquals(1,source.bodyReads)
            store.close();store = store();observations = ProcessorWorkspaceObservations(store)
            assertEquals(failed,observations.lastAttempt())
            val retry = Source(observations);retry.cursorValue = "cursor-later";retry.files = listOf(metadata("a"))
            rejects { ProcessorWorkspaceInventory(observations,retry).prepare { _,_ -> fail("A missing unread document was forgotten") } }
            assertEquals(0,retry.bodyReads);assertEquals("cursor-first",observations.lastAttempt()?.firstUnresolved)
        } finally { store.close() }
    }

    @Test fun preparedReadsStayPendingAndOldTokensAreFenced() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val source = Source(observations)
            val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,bytes -> assertArrayEquals("{}".toByteArray(),bytes) }
            assertEquals(2,prepared.reads.size);assertTrue(prepared.attempt.inventoryListed)
            assertThrows(UnsupportedOperationException::class.java) { (prepared.reads as MutableList<*>).clear() }
            val next = observations.begin(pin.documentSha256)
            assertTrue(next.revision > prepared.attempt.revision);assertNotEquals(next.id,prepared.attempt.id)
            assertFalse(next.inventoryListed);assertNull(next.inventoryStart)
            assertEquals("cursor-first",next.firstUnresolved)
            rejects { observations.reads(prepared.attempt) }
            rejects { observations.start(prepared.attempt,DriveWorkspaceCursor(pin.workspace.accountId,null,"another")) }
            assertEquals(next,observations.lastAttempt())
        } finally { store.close() }
    }

    @Test fun changedContentNeverOverwritesReadEvidence() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val old = ProcessorWorkspaceInventory(observations,Source(observations)).prepare { _,_ -> }
            var next = observations.start(observations.begin(pin.documentSha256),DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor-later"))
            val rows = listOf(candidate(metadata("a",2)),candidate(metadata("b")))
            next = observations.rememberCandidates(next,rows);next = observations.sealInventory(next,rows)
            rejects { observations.rememberRead(next,rows[0],"[]".toByteArray()) }
            rejects { observations.rememberCandidates(next,listOf(candidate(metadata("a")))) }
            rejects { observations.reads(next) }
            sql(store) { db -> db.rawQuery("SELECT document FROM workspace_read WHERE file_id='a'",null).use { row -> assertTrue(row.moveToFirst());assertTrue(String(row.getBlob(0)).contains(old.reads.first().sha256)) } }
            assertEquals(next,observations.lastAttempt())
        } finally { store.close() }
    }

    @Test fun damagedCandidateCannotBeWashedAway() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            var attempt = observations.start(observations.begin(pin.documentSha256),DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor"))
            attempt = observations.rememberCandidates(attempt,listOf(candidate(metadata("a")),candidate(metadata("b"))))
            sql(store) { it.execSQL("UPDATE workspace_candidate SET document=x'0000' WHERE file_id='b'") }
            try { observations.begin(pin.documentSha256);fail("Corrupted body was silently washed away") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store) { it.execSQL("DELETE FROM workspace_candidate WHERE file_id='b'") }
            try { observations.lastAttempt();fail("Unread metadata disappeared") } catch (_: ProcessorLibraryStateCorrupt) { }
            assertNotNull(attempt)
        } finally { store.close() }
    }

    @Test fun failedWriteKeepsTheOldAttemptAndRows() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val attempt = observations.start(observations.begin(pin.documentSha256),DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor"))
            sql(store) { it.execSQL("CREATE TRIGGER reject_attempt BEFORE UPDATE ON workspace_attempt BEGIN SELECT RAISE(ABORT,'synthetic failure'); END") }
            try { observations.rememberCandidates(attempt,listOf(candidate(metadata("a"))));fail("Failed native transaction was acknowledged") } catch (_: SQLiteException) { }
            assertEquals(attempt,observations.lastAttempt())
            sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_candidate",null).use { assertTrue(it.moveToFirst());assertEquals(0,it.getInt(0)) } }
        } finally { store.close() }
    }

    @Test fun admissionFailsBeforeAnyNetworkCall() = runBlocking {
        val capacity = Capacity();val store = store(capacity)
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val previous = observations.begin(pin.documentSha256)
            val source = Source(observations);capacity.bytes = 0
            rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> } }
            assertEquals(0,source.sourceCalls);assertEquals(previous,observations.lastAttempt())
            capacity.bytes = 1024L * 1024 * 1024;source.connectionSha256 = "0".repeat(64)
            rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> } }
            assertEquals(0,source.sourceCalls);assertEquals(previous,observations.lastAttempt())
        } finally { store.close() }
    }

    @Test fun replacedReadCannotCommitOrVerify() = runBlocking {
        val left = store();val right = store()
        try {
            approve(left);val observations = ProcessorWorkspaceObservations(left);val other = ProcessorWorkspaceObservations(right)
            val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>();var retired = false;var verified = 0
            val source = Source(observations);source.beforeRead = { try { withContext(NonCancellable) { entered.complete(Unit);release.await() } } finally { retired = true } }
            supervisorScope {
                val owner = async { try { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> verified++ };false } catch (_: ProcessorWorkspaceConflict) { true } }
                try { withTimeout(5000) { entered.await() };val replacement = other.begin(pin.documentSha256);release.complete(Unit);assertTrue(owner.await());assertEquals(replacement,other.lastAttempt()) }
                finally { release.complete(Unit);owner.cancel();try { owner.await() } catch (_: CancellationException) { } }
            }
            assertTrue(retired);assertEquals(0,verified)
        } finally { left.close();right.close() }
    }

    @Test fun canceledReadJoinsAndKeepsCandidates() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>();var retired = false
            val source = Source(observations);source.beforeRead = { try { withContext(NonCancellable) { entered.complete(Unit);release.await() } } finally { retired = true } }
            supervisorScope {
                val owner = async { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> fail("Canceled read reached verification") } }
                try { withTimeout(5000) { entered.await() };owner.cancel();assertFalse(owner.isCompleted);release.complete(Unit);try { owner.await();fail("Canceled owner completed") } catch (_: CancellationException) { } }
                finally { release.complete(Unit);owner.cancel();try { owner.await() } catch (_: CancellationException) { } }
            }
            assertTrue(retired);assertEquals(0,sqlReadCount(store));assertTrue(checkNotNull(observations.lastAttempt()).inventoryListed)
        } finally { store.close() }
    }

    @Test fun schemaThreeKeepsAuthorityAndAccount() = runBlocking {
        var store = store()
        try {
            approve(store)
            val body = CanonicalProtocolJson().encode("""{"format":"mpa","major":1,"minor":0,"libraryId":"${pin.libraryId}","actorId":"${pin.controllerId}","epoch":"1","sequence":"7","kind":"library-publisher-authorization","requiredCapabilities":["library.publishers.v1"],"payload":{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"publisher":null}}""".toByteArray())
            val signer = ProtocolDocumentSigner(key.private,key.public);val bytes = SignedProtocolDocuments(setOf("library.publishers.v1")).encode(body,signer.sign(body))
            ProcessorLibraryControllerJournal(store).accept(bytes);store.close()
            sql(store) { db -> db.execSQL("DROP TABLE workspace_history");db.execSQL("DROP TABLE workspace_replay");db.execSQL("DROP TABLE workspace_event");db.execSQL("DROP TABLE workspace_read");db.execSQL("DROP TABLE workspace_candidate");db.execSQL("DROP TABLE workspace_attempt");db.version = 3 }
            store = store();assertEquals(7L,ProcessorLibraryControllerJournal(store).current().publisher?.sequence)
            assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            val observations = ProcessorWorkspaceObservations(store);assertNull(observations.lastAttempt())
            assertEquals(1L,observations.begin(pin.documentSha256).revision)
            sql(store) { assertEquals(5,it.version) }
        } finally { store.close() }
    }

    @Test fun missingStateCannotResetAcceptedTrust() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);observations.begin(pin.documentSha256)
            sql(store) { it.execSQL("DELETE FROM workspace_attempt") }
            try { observations.begin(pin.documentSha256);fail("Lost scan owner was recreated") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store) { it.execSQL("DELETE FROM approved_pin");it.execSQL("DELETE FROM client_meta WHERE key='approved_pin_sha256'");it.execSQL("DELETE FROM credential_selector") }
            try { store.registration();fail("Workspace marker was treated as first install") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close() }
    }

    @Test fun malformedBatchesNeverChangePendingEvidence() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val attempt = observations.start(observations.begin(pin.documentSha256),DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor"))
            val value = candidate(metadata("a"))
            rejects { observations.rememberCandidates(attempt,listOf(value,value)) }
            rejects { observations.rememberCandidates(attempt,List(101) { candidate(metadata("item-$it")) }) }
            assertEquals(attempt,observations.lastAttempt())
            val changed = observations.rememberCandidates(attempt,listOf(value))
            rejects { observations.rememberCandidates(changed,listOf(value.copy(mimeType="application/gzip"))) }
            rejects { observations.rememberCandidates(changed,listOf(value.copy(bytes=3))) }
            assertEquals(changed,observations.lastAttempt())
        } finally { store.close() }
    }

    @Test fun repeatedEmptyPageTokensFailWithoutARead() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);var pages = 0
            val source = object : ProcessorWorkspaceInventorySource {
                override val connectionSha256 = pin.documentSha256
                override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor")
                override suspend fun page(token: String?): DriveWorkspacePage { pages++;return DriveWorkspacePage(emptyList(),"repeat",false) }
                override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray { fail("Cyclic inventory downloaded content");return byteArrayOf() }
            }
            rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> } }
            assertEquals(2,pages);assertFalse(checkNotNull(observations.lastAttempt()).inventoryListed)
        } finally { store.close() }
    }

    @Test fun unreadableIdentitiesCannotDisappear() = runBlocking {
        for ((index,size) in listOf<Long?>(null,0,256L * 1024 + 1).withIndex()) {
            val store = ProcessorLibraryStateStore(context,StorageBudget(Capacity()),File(context.noBackupFilesDir,"unreadable-$index"))
            try {
                approve(store);val observations = ProcessorWorkspaceObservations(store);val source = Source(observations)
                source.files = listOf(DriveWorkspaceMetadata("bad","application/json",1,size,listOf("test-workspace"),false,null,true))
                rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> fail("Unusable metadata reached verification") } }
                assertEquals(0,source.bodyReads)
                assertTrue(checkNotNull(observations.lastAttempt()).inventoryListed)
                source.files = emptyList()
                rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> } }
                assertEquals(0,source.bodyReads)
            } finally { store.close() }
        }
    }

    @Test fun unknownLengthCanBeRefinedWithoutLosingIdentity() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val source = Source(observations)
            source.files = listOf(DriveWorkspaceMetadata("a","application/json",1,null,listOf("test-workspace"),false,null,true))
            rejects { ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> } }
            source.files = listOf(metadata("a",2))
            val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> }
            assertEquals(1,prepared.reads.size);assertEquals(2L,prepared.reads.single().candidate.bytes)
            assertEquals("cursor-first",prepared.attempt.firstUnresolved)
        } finally { store.close() }
    }

    @Test fun missingOrModifiedEventsCannotRebuildTrust() = runBlocking {
        val store = store()
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val prepared = ProcessorWorkspaceInventory(observations,Source(observations)).prepare { _,_ -> }
            sql(store) { it.execSQL("UPDATE workspace_event SET document=x'0000' WHERE sequence=1") }
            try { observations.reads(prepared.attempt);fail("Changed earlier event was trusted") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store) { it.execSQL("DELETE FROM workspace_event WHERE sequence=1") }
            try { observations.begin(pin.documentSha256);fail("Lost event history was reset") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close() }
    }

    @Test fun recordBoundedMetadataAdmissionCost() = runBlocking {
        for (count in listOf(64,256)) {
            val store = ProcessorLibraryStateStore(context,StorageBudget(Capacity()),File(context.noBackupFilesDir,"bench-$count"))
            try {
                approve(store);val observations = ProcessorWorkspaceObservations(store)
                val rows = List(count) { metadata("item-${it.toString().padStart(4,'0')}") }
                val source = object : ProcessorWorkspaceInventorySource {
                    override val connectionSha256 = pin.documentSha256
                    override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor")
                    override suspend fun page(token: String?): DriveWorkspacePage {
                        val offset = token?.toInt() ?: 0;val end = minOf(offset + 100,rows.size)
                        return DriveWorkspacePage(rows.subList(offset,end),if(end < rows.size) end.toString() else null,false)
                    }
                    override suspend fun readCaptured(expected: DriveWorkspaceMetadata) = "{}".toByteArray()
                }
                val start = System.nanoTime()
                val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> }
                val elapsed = (System.nanoTime() - start) / 1_000_000
                assertEquals(count,prepared.reads.size)
                println("MPA_NATIVE_PENDING_INVENTORY_BENCH records=$count elapsedMs=$elapsed platform=${System.getProperty("os.name")?.replace(' ','_') ?: "unknown"}RobolectricAPI28 syntheticBodies=true")
            } finally { store.close() }
        }
    }

    private fun sqlReadCount(store: ProcessorLibraryStateStore): Int {
        var count = -1;sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_read",null).use { assertTrue(it.moveToFirst());count = it.getInt(0) } };return count
    }
}
