package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChange
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChangesPage
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
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
import kotlinx.coroutines.withContext
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorWorkspaceHistoryTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("actual-account","workspace"),key.public.encoded)
    private class Capacity : StorageBudget.Capacity {
        var bytes = 1024L * 1024 * 1024
        override fun available(directory: File) = bytes
        override fun allocatable(directory: File) = bytes
        override fun allocate(directory: File,bytes: Long) { error("History must never evict originals.") }
    }
    private fun store(capacity: Capacity = Capacity()) = ProcessorLibraryStateStore(context,StorageBudget(capacity))
    private fun cleanup(directory: File) {
        check(directory.canonicalFile.parentFile == context.noBackupFilesDir.canonicalFile && directory.name == "processor-client")
        check(!directory.exists() || directory.deleteRecursively())
    }
    private fun cursor(token: String = "start",drive: String? = null) = DriveWorkspaceCursor(pin.workspace.accountId,drive,token)
    private fun metadata(id: String = "a",version: Long = 2,parent: String = "workspace",bytes: Long? = 2,downloadable: Boolean? = true,mime: String = "application/json") = DriveWorkspaceMetadata(id,mime,version,bytes,listOf(parent),false,null,downloadable)
    private fun change(meta: DriveWorkspaceMetadata = metadata()) = DriveWorkspaceChange("file",meta.id,false,meta)
    private fun page(from: DriveWorkspaceCursor,changes: List<DriveWorkspaceChange> = emptyList(),end: Boolean = true,token: String = "end") =
        DriveWorkspaceChangesPage(changes,if(end) null else from.copy(token=token),if(end) from.copy(token=token) else null)
    private fun source(action: suspend (DriveWorkspaceCursor)->DriveWorkspaceChangesPage) = object : ProcessorWorkspaceHistorySource { override suspend fun changes(cursor: DriveWorkspaceCursor) = action(cursor) }
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val reader = object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount(pin.workspace.accountId,"synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        }
        val connection = ProcessorLibraryConnection(store,reader);connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private suspend fun prepared(observations: ProcessorWorkspaceObservations,from: DriveWorkspaceCursor = cursor(),files: List<DriveWorkspaceMetadata> = listOf(metadata())): ProcessorWorkspacePrepared {
        val source = object : ProcessorWorkspaceInventorySource {
            override val connectionSha256 = pin.documentSha256
            override suspend fun cursor() = from
            override suspend fun page(token: String?) = DriveWorkspacePage(files,null,false)
            override suspend fun readCaptured(expected: DriveWorkspaceMetadata) = "{}".toByteArray()
        }
        return ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> }
    }
    private fun sql(store: ProcessorLibraryStateStore,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(store.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private fun document(store: ProcessorLibraryStateStore,table: String): com.google.gson.JsonObject {
        var result: com.google.gson.JsonObject? = null
        sql(store) { db -> db.rawQuery("SELECT document FROM $table",null).use { row -> assertTrue(row.moveToFirst());result = JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject } }
        return checkNotNull(result)
    }
    private suspend fun rejects(action: suspend ()->Unit) { try { action();fail("Unverified history was accepted") } catch (_: IOException) { } }

    @Test fun replayedOlderEventsCannotRewindReadCandidatesAndNothingActivates() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val prepared = prepared(observations)
            val reader = ProcessorWorkspaceHistory(observations,source { from -> assertEquals(cursor(),from);page(from,listOf(change(metadata(version=1)))) })
            val batch = reader.replay(prepared)
            assertTrue(batch.state.atEnd);assertFalse(batch.state.active);assertEquals("end",batch.state.cursor.token)
            assertEquals("start",batch.attempt.firstUnresolved);assertEquals(1L,batch.state.totalPages)
            assertEquals(prepared.reads,observations.reads(batch.attempt));assertEquals("1",document(store,"workspace_history")["occurrences"].asString)
            assertEquals(batch.attempt,observations.lastAttempt());rejects { observations.reads(prepared.attempt) }
        } finally { store.close() }
    }
    @Test fun wholePageEvidenceCommitsBeforeRecoveryErrorAndCannotBeWashedByRetry() = runBlocking {
        var store = store();try {
            approve(store);var observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val removed = DriveWorkspaceChange("file","unknown-removed",true,null)
            rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(change(),removed)) }).replay(ready) }
            sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_history",null).use { row -> assertTrue(row.moveToFirst());assertEquals(2,row.getInt(0)) } }
            val failed = checkNotNull(observations.lastAttempt());assertEquals("end",document(store,"workspace_replay")["cursor"].asJsonObject["token"].asString)
            store.close();store = store();observations = ProcessorWorkspaceObservations(store);assertEquals(failed,observations.lastAttempt())
            val retry = prepared(observations,cursor("new-start"));var called = false
            rejects { ProcessorWorkspaceHistory(observations,source { called = true;page(it) }).replay(retry) }
            assertFalse(called);assertEquals("start",observations.lastAttempt()?.firstUnresolved)
        } finally { store.close() }
    }
    @Test fun movedTrashedMissingLengthAndLostAccessRemainStickyRecovery() = runBlocking {
        for(bad in listOf(metadata(parent="outside"),metadata(bytes=null),metadata(bytes=0),metadata(downloadable=false),metadata(bytes=300000))) {
            val store = store();try {
                approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
                rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(change(bad))) }).replay(ready) }
                assertTrue(document(store,"workspace_history")["recovery"].asBoolean)
            } finally { store.close();cleanup(store.directory) }
        }
    }
    @Test fun freshMetadataCandidatesPersistBeforeInventoryChangedFailure() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(change(metadata("new")),change())) }).replay(ready) }
            val failed = checkNotNull(observations.lastAttempt());assertFalse(failed.inventoryListed)
            sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_candidate WHERE file_id='new'",null).use { row -> assertTrue(row.moveToFirst());assertEquals(1,row.getInt(0)) } }
            assertTrue(document(store,"workspace_replay")["changed"].asBoolean)
            val retry = prepared(observations,cursor("later"),listOf(metadata(),metadata("new")))
            val result = ProcessorWorkspaceHistory(observations,source { from -> assertEquals("end",from.token);page(from,token="new-end") }).replay(retry)
            assertEquals("start",result.attempt.firstUnresolved);assertEquals(2,observations.reads(result.attempt).size)
        } finally { store.close() }
    }
    @Test fun repeatedChangesCoalesceCountsButNeverClearRecovery() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(DriveWorkspaceChange("file","a",true,null),change())) }).replay(ready) }
            val history = document(store,"workspace_history");assertTrue(history["recovery"].asBoolean);assertEquals("2",history["occurrences"].asString)
            assertNotEquals(history["first"].asString,history["last"].asString)
        } finally { store.close() }
    }
    @Test fun boundedBatchResumesAfterRestartWithoutPermanent128PageStop() = runBlocking {
        var store = store();try {
            approve(store);var observations = ProcessorWorkspaceObservations(store);var requests = 0
            val first = ProcessorWorkspaceHistory(observations,source { from -> requests++;page(from,end=false,token="p$requests") }).replay(prepared(observations))
            assertEquals(128,requests);assertFalse(first.state.active);assertFalse(first.state.atEnd);assertEquals(128L,first.state.totalPages)
            store.close();store = store();observations = ProcessorWorkspaceObservations(store)
            val ready = prepared(observations,cursor("fresh-start"))
            val last = ProcessorWorkspaceHistory(observations,source { from -> assertEquals("p128",from.token);page(from,token="final-end") }).replay(ready)
            assertEquals(129L,last.state.totalPages);assertEquals(1,last.state.batchPages);assertEquals("start",last.state.start.token);assertTrue(last.state.atEnd)
        } finally { store.close() }
    }
    @Test fun rejectedFinalFenceRetainsNewIdentityWithoutAdvancingCursor() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val initial = ProcessorWorkspaceHistory(observations,source { page(it) }).replay(ready)
            rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(change(metadata("new"))),token="must-not-advance") }).checkUnchanged(initial,ready.reads) }
            val replay = document(store,"workspace_replay");assertEquals("end",replay["cursor"].asJsonObject["token"].asString);assertFalse(replay["end"].asBoolean);assertTrue(replay["changed"].asBoolean)
            sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_candidate WHERE file_id='new'",null).use { row -> assertTrue(row.moveToFirst());assertEquals(1,row.getInt(0)) } }
            rejects { observations.reads(initial.attempt) }
        } finally { store.close() }
    }
    @Test fun unchangedFenceRetainsActualNewCursorAndEarliestInterval() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val first = ProcessorWorkspaceHistory(observations,source { page(it) }).replay(ready)
            val last = ProcessorWorkspaceHistory(observations,source { from -> assertEquals("end",from.token);page(from,token="fence") }).checkUnchanged(first,ready.reads)
            assertTrue(last.state.atEnd);assertEquals("fence",last.state.cursor.token);assertEquals("start",last.attempt.firstUnresolved)
            assertEquals(ready.reads,observations.reads(last.attempt))
        } finally { store.close() }
    }
    @Test fun unrelatedAncestorFolderForcesNewFenceWithoutInventingPermanentRemoval() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val first = ProcessorWorkspaceHistory(observations,source { page(it) }).replay(ready)
            rejects { ProcessorWorkspaceHistory(observations,source { page(it,listOf(change(metadata("ancestor",parent="elsewhere",mime="application/vnd.google-apps.folder")))) }).checkUnchanged(first,ready.reads) }
            assertFalse(document(store,"workspace_history")["recovery"].asBoolean);assertTrue(document(store,"workspace_replay")["changed"].asBoolean)
        } finally { store.close() }
    }
    @Test fun foreignCorpusAndPrincipalCannotAdvanceOrRelabelRetainedInterval() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations,cursor(drive="actual-drive"))
            rejects { ProcessorWorkspaceHistory(observations,source { DriveWorkspaceChangesPage(emptyList(),null,cursor("evil",drive="foreign")) }).replay(ready) }
            assertEquals("start",document(store,"workspace_replay")["cursor"].asJsonObject["token"].asString)
            val next = observations.begin(pin.documentSha256);rejects { observations.start(next,cursor("new",drive="foreign")) };rejects { observations.start(next,cursor("new").copy(accountId="foreign")) }
            assertNull(observations.lastAttempt()?.inventoryStart);assertEquals("actual-drive",observations.lastAttempt()?.unresolvedDriveId)
        } finally { store.close() }
    }
    @Test fun staleAndCanceledPhysicalReadsJoinWithoutCommittingOrVerifying() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>();var retired = false
            supervisorScope {
                val owner = async { ProcessorWorkspaceHistory(observations,source { from -> withContext(NonCancellable) { entered.complete(Unit);release.await();retired = true;page(from,listOf(DriveWorkspaceChange("file","removed",true,null))) } }).replay(ready) }
                entered.await();owner.cancel();val next = observations.begin(pin.documentSha256)
                assertFalse(owner.isCompleted);release.complete(Unit)
                try { owner.await();fail("Canceled replay completed") } catch (_: CancellationException) { }
                assertTrue(retired);assertEquals(next,observations.lastAttempt())
                sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_history",null).use { row -> assertTrue(row.moveToFirst());assertEquals(0,row.getInt(0)) } }
            }
        } finally { store.close() }
    }
    @Test fun lowStorageRollsBackEntireObservedPageIncludingCursorAndKnownIds() = runBlocking {
        val capacity = Capacity();val store = store(capacity);try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations);var before: ProcessorWorkspaceAttempt? = null
            rejects { ProcessorWorkspaceHistory(observations,source { from -> before = observations.lastAttempt();capacity.bytes = 0;page(from,listOf(change(metadata("new")))) }).replay(ready) }
            capacity.bytes = 1024L * 1024 * 1024;assertEquals(before,observations.lastAttempt())
            sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_history",null).use { row -> assertTrue(row.moveToFirst());assertEquals(0,row.getInt(0)) } }
        } finally { store.close() }
    }
    @Test fun damagedHistoryAndReplayBodiesCannotBeWashedByNewOwner() = runBlocking {
        for(table in listOf("workspace_history","workspace_replay")) {
            val store = store();try {
                approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
                ProcessorWorkspaceHistory(observations,source { page(it,listOf(change())) }).replay(ready)
                sql(store) { it.execSQL("UPDATE $table SET document=x'0000'") }
                try { observations.begin(pin.documentSha256);fail("Corrupted replay was replaced") } catch (_: ProcessorLibraryStateCorrupt) { }
            } finally { store.close();cleanup(store.directory) }
        }
    }
    @Test fun realSchema4MigrationPreservesLegacyGlobalCursorAndExactEventBytes() = runBlocking {
        var store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            var priorEvents = listOf<ByteArray>()
            sql(store) { db ->
                db.execSQL("DROP TABLE workspace_history");db.execSQL("DROP TABLE workspace_replay")
                val legacy = document(store,"workspace_attempt");listOf("startDrive","unresolvedDrive","history","replay").forEach { legacy.remove(it) }
                val bytes = CanonicalProtocolJson(65536).encode(legacy.toString().toByteArray(Charsets.UTF_8))
                db.execSQL("UPDATE workspace_attempt SET document=?,sha256=?",arrayOf(bytes,ProcessorWorkspaceObservations.hash(bytes)))
                db.rawQuery("SELECT document FROM workspace_event ORDER BY sequence",null).use { rows -> val list = arrayListOf<ByteArray>();while(rows.moveToNext()) list.add(rows.getBlob(0));priorEvents = list }
                db.execSQL("DROP INDEX workspace_event_lookup");db.execSQL("ALTER TABLE workspace_event RENAME TO old_events")
                db.execSQL("CREATE TABLE workspace_event(sequence INTEGER NOT NULL PRIMARY KEY CHECK(sequence>0),kind TEXT NOT NULL CHECK(kind IN ('candidate','read')),file_id TEXT NOT NULL CHECK(length(file_id) BETWEEN 1 AND 256),document BLOB NOT NULL CHECK(length(document) BETWEEN 1 AND 2048),sha256 TEXT NOT NULL CHECK(length(sha256)=64))")
                db.execSQL("CREATE INDEX workspace_event_lookup ON workspace_event(kind,file_id,sequence)");db.execSQL("INSERT INTO workspace_event SELECT * FROM old_events");db.execSQL("DROP TABLE old_events");db.version = 4
            }
            store.close();store = store();val restored = ProcessorWorkspaceObservations(store);assertEquals(ready.attempt,restored.lastAttempt());assertEquals(pin.encode(),store.current()?.encode())
            sql(store) { db -> assertEquals(6,db.version);db.rawQuery("SELECT document FROM workspace_event ORDER BY sequence",null).use { rows -> var index = 0;while(rows.moveToNext()) assertArrayEquals(priorEvents[index++],rows.getBlob(0));assertEquals(priorEvents.size,index) } }
            val next = restored.begin(pin.documentSha256);rejects { restored.start(next,cursor("later",drive="new-drive")) };assertNull(restored.lastAttempt()?.unresolvedDriveId)
            val started = restored.start(next,cursor("later"));assertEquals("start",started.firstUnresolved)
        } finally { store.close() }
    }
    @Test fun omittedReadIdentitiesCannotChangeReplayOrFinalFenceSemantics() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations);var calls = 0
            val reader = ProcessorWorkspaceHistory(observations,source { calls++;page(it) })
            rejects { reader.replay(ProcessorWorkspacePrepared(ready.attempt,emptyList())) };assertEquals(0,calls)
            val retry = prepared(observations,cursor("later"));val batch = reader.replay(retry);assertEquals(1,calls)
            rejects { reader.checkUnchanged(batch,emptyList()) };assertEquals(1,calls);assertEquals(batch.attempt,observations.lastAttempt())
        } finally { store.close() }
    }
    @Test fun repeatedOpaqueCursorIsNotMistakenForCompletedHistory() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations);var calls = 0
            rejects { ProcessorWorkspaceHistory(observations,source { from -> calls++;page(from,end=false,token=if(calls == 1) "next" else "start") }).replay(ready) }
            assertEquals(2,calls);assertFalse(document(store,"workspace_replay")["end"].asBoolean)
            assertEquals("start",observations.lastAttempt()?.firstUnresolved)
        } finally { store.close() }
    }
    @Test fun uncanceledLateOwnerCannotCommitAcrossReplacement() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);val ready = prepared(observations)
            val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>()
            supervisorScope {
                val old = async { ProcessorWorkspaceHistory(observations,source { entered.complete(Unit);release.await();page(it,listOf(DriveWorkspaceChange("file","removed",true,null))) }).replay(ready) }
                entered.await();val next = observations.begin(pin.documentSha256);release.complete(Unit);rejects { old.await() };assertEquals(next,observations.lastAttempt())
                sql(store) { db -> db.rawQuery("SELECT count(*) FROM workspace_history",null).use { row -> assertTrue(row.moveToFirst());assertEquals(0,row.getInt(0)) } }
            }
        } finally { store.close() }
    }

}
