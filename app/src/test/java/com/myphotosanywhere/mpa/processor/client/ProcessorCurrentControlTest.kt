package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.processor.protocol.*
import java.io.File
import java.io.IOException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ProcessorCurrentControlTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-current-control.json"))
        .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
    private val pin = LibraryConnectionDescriptor.decode(fixture["code"].asString)
    private fun head(index: Int = 0) = fixture["valid"].asJsonArray[index].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8)
    private class Capacity : StorageBudget.Capacity {
        var bytes = 1024L * 1024 * 1024
        override fun available(directory: File) = bytes
        override fun allocatable(directory: File) = bytes
        override fun allocate(directory: File,bytes: Long) { error("Current control cannot evict originals.") }
    }
    private fun store(capacity: Capacity = Capacity()) = ProcessorLibraryStateStore(context,StorageBudget(capacity),File(context.noBackupFilesDir,"cc"))
    private suspend fun approve(store: ProcessorLibraryStateStore,email: String = "synthetic@example.invalid",descriptor: LibraryConnectionDescriptor = pin) {
        val connection = ProcessorLibraryConnection(store,object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount(pin.workspace.accountId,email)
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        });connection.approve(connection.review(descriptor.encode()),descriptor.documentSha256)
    }
    private inner class Source : ProcessorWorkspaceCapturedSource {
        override var connectionSha256 = pin.documentSha256
        var body = head();var version = 1L;var drive: String? = null
        var captures = 0;var reads = 0
        var beforeCapture: (suspend ()->Unit)? = null;var beforeBody: (suspend ()->Unit)? = null
        var transform: (DriveWorkspaceMetadata)->DriveWorkspaceMetadata = { it }
        fun metadata() = transform(DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"application/json",version,body.size.toLong(),listOf(pin.workspace.folderId),false,null,true,drive))
        override suspend fun capture(fileId: String): DriveWorkspaceMetadata { assertEquals(pin.currentFileId,fileId);captures++;beforeCapture?.invoke();return metadata() }
        override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray { reads++;beforeBody?.invoke();return body }
        override suspend fun cursor(): DriveWorkspaceCursor = error("Control reads must not scan account changes.")
        override suspend fun page(token: String?): DriveWorkspacePage = error("Control reads must not list or search for equal copies.")
    }
    private fun reader(store: ProcessorLibraryStateStore,source: Source) = ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,pin)
    private fun sql(store: ProcessorLibraryStateStore,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(store.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private suspend fun rejected(action: suspend ()->Unit) { try { action();fail("Unverified or retired control became usable") } catch (_: IOException) { } catch (_: ProcessorProtocolException) { } }
    private fun phase(store: ProcessorLibraryStateStore): String { var result="";sql(store) { it.rawQuery("SELECT document FROM current_control_attempt",null).use { row -> assertTrue(row.moveToFirst());result=JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject["phase"].asString } };return result }

    @Test fun exactCapturePersistsBeforeBodyAndFinalFenceRetiresIt() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);source.beforeBody={ assertEquals(if(source.reads==1) "reading" else "captured",phase(store)) }
            val captured=reader.capture();assertEquals(11L,captured.head.sequence);reader.assertCaptured(captured)
            assertEquals("fenced",reader.fence(captured).phase);val calls=source.captures
            rejected { reader.assertCaptured(captured) };rejected { reader.fence(captured) }
            assertEquals(calls,source.captures);assertEquals(2,source.reads)
            sql(store) { it.rawQuery("SELECT count(*) FROM workspace_candidate",null).use { row -> assertTrue(row.moveToFirst());assertEquals(0,row.getInt(0)) } }
        } finally { reader.close();store.close() }
    }

    @Test fun headAndActualFileVersionCannotRewindAcrossRestart() = runBlocking {
        var store=store();val source=Source();var reader=reader(store,source)
        try {
            approve(store);reader.capture();reader.close();store.close();store=store();reader=reader(store,source)
            source.body=head(1);source.version=2;reader.capture()
            source.body=head();source.version=3;rejected { reader.capture() }
            source.body=head(1);source.version=2;val reads=source.reads;rejected { reader.capture() };assertEquals(reads,source.reads)
            source.version=3;rejected { reader.capture() };source.version=4;assertEquals(12L,reader.capture().head.sequence)
            sql(store) { it.rawQuery("SELECT count(*) FROM current_control_head",null).use { row -> assertTrue(row.moveToFirst());assertEquals(2,row.getInt(0)) } }
        } finally { reader.close();store.close() }
    }

    @Test fun malformedReadKeepsCapturedVersionForRetry() = runBlocking {
        var store=store();val source=Source();var reader=reader(store,source)
        try {
            approve(store);source.version=7;source.body="{}".toByteArray();rejected { reader.capture() };assertEquals("read",phase(store))
            reader.close();store.close();store=store();reader=reader(store,source)
            source.version=6;source.body=head();val reads=source.reads;rejected { reader.capture() };assertEquals(reads,source.reads)
            source.version=7;rejected { reader.capture() };source.version=8;assertEquals(11L,reader.capture().head.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun forkAndOlderOrphanPolicyNeverOverwriteTheSignedHead() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);reader.capture()
            for(index in 1..3) {
                source.version++;source.body=fixture["invalidTransitions"].asJsonArray[index].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8)
                rejected { reader.capture() }
            }
            sql(store) { it.rawQuery("SELECT envelope FROM current_control_head",null).use { row -> assertTrue(row.moveToFirst());assertArrayEquals(head(),row.getBlob(0));assertFalse(row.moveToNext()) } }
        } finally { reader.close();store.close() }
    }

    @Test fun changedFinalFenceIsRetainedAndCannotBeRetriedAsUnchanged() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();source.version=2
            rejected { reader.fence(captured) };assertEquals("blocked",phase(store));val calls=source.captures
            source.version=1;rejected { reader.fence(captured) };assertEquals(calls,source.captures)
            rejected { reader.capture() };source.version=2;assertEquals(11L,reader.capture().head.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun changedFenceBytesBlockTheOldCapture() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();source.body=source.body.copyOf().also { it[it.lastIndex]=(it.last().toInt() xor 1).toByte() }
            rejected { reader.fence(captured) };assertEquals("blocked",phase(store));val reads=source.reads
            rejected { reader.fence(captured) };assertEquals(reads,source.reads)
        } finally { reader.close();store.close() }
    }

    @Test fun newOwnerRetiresOldCaptureAndDoesNotAdoptItsContext() = runBlocking {
        val store=store();val source=Source();val first=reader(store,source);val second=reader(store,source)
        try {
            approve(store);val old=first.capture();val current=second.capture();val calls=source.captures
            rejected { first.fence(old) };rejected { second.fence(old) };assertEquals(calls,source.captures)
            assertEquals("fenced",second.fence(current).phase)
        } finally { first.close();second.close();store.close() }
    }

    @Test fun staleMetadataCaptureCannotBeginItsBodyRead() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);source.beforeCapture={ ProcessorCurrentControlObservations(store).begin(pin.documentSha256) }
            rejected { reader.capture() };assertEquals(0,source.reads)
        } finally { reader.close();store.close() }
    }

    @Test fun closeJoinsUncooperativeBodyAndDeniesItsLateAdmission() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);supervisorScope {
                val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
                source.beforeBody={ withContext(NonCancellable) { entered.complete(Unit);release.await() } }
                val read=async { runCatching { reader.capture() } };withTimeout(5000) { entered.await() }
                val closing=async { reader.close() };kotlinx.coroutines.yield();assertFalse(closing.isCompleted)
                release.complete(Unit);assertTrue(withTimeout(5000) { read.await() }.exceptionOrNull() is IllegalStateException)
                withTimeout(5000) { closing.await() };assertEquals("reading",phase(store))
            }
        } finally { reader.close();store.close() }
    }

    @Test fun cancellationJoinsPhysicalBodyBeforeReturning() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);supervisorScope {
                val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
                source.beforeBody={ withContext(NonCancellable) { entered.complete(Unit);release.await() } }
                val read=async { reader.capture() };withTimeout(5000) { entered.await() };read.cancel();assertFalse(read.isCompleted)
                release.complete(Unit);withTimeout(5000) { read.join() };assertTrue(read.isCancelled);assertEquals("reading",phase(store))
            }
        } finally { reader.close();store.close() }
    }

    @Test fun localTamperingAndMissingPinCannotResetHighWater() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);reader.capture()
            sql(store) { it.execSQL("DELETE FROM client_meta WHERE key='current_control_state_sha256'") }
            try { reader.capture();fail("Missing integrity marker was rebuilt") } catch (_: ProcessorLibraryStateCorrupt) { }
            sql(store) { it.execSQL("DELETE FROM approved_pin");it.execSQL("DELETE FROM client_meta WHERE key='approved_pin_sha256'") }
            try { store.current();fail("Missing pin was treated as unconfigured") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { reader.close();store.close() }
    }

    @Test fun lowStorageRollsBackBeginAndKeepsOriginalAndHead() = runBlocking {
        val capacity=Capacity();val store=store(capacity);val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();val original=File(store.directory,"original.jpg").apply { writeBytes(byteArrayOf(1,3,5)) }
            capacity.bytes=0;val calls=source.captures;rejected { reader.capture() };assertEquals(calls,source.captures)
            capacity.bytes=1024L*1024*1024;reader.assertCaptured(captured);assertArrayEquals(byteArrayOf(1,3,5),original.readBytes())
        } finally { reader.close();store.close() }
    }

    @Test fun newCredentialSelectorRetiresOldCaptureButKeepsHighWater() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val captured=reader.capture();approve(store,"renewed@example.invalid");val calls=source.captures
            rejected { reader.fence(captured) };assertEquals(calls,source.captures);assertEquals(11L,reader.capture().head.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun schemaSixMigrationPreservesApprovalJournalAndOriginal() = runBlocking {
        var store=store();val source=Source()
        try {
            approve(store);val pending=ProcessorWorkspaceObservations(store).begin(pin.documentSha256)
            val original=File(store.directory,"original.jpg").apply { writeBytes(byteArrayOf(6,2,4)) };store.close()
            sql(store) { it.execSQL("DROP TABLE current_control_attempt");it.execSQL("DROP TABLE current_control_head");it.version=6 }
            store=store();assertEquals(pin.encode(),store.current()?.encode());assertEquals(pending,ProcessorWorkspaceObservations(store).lastAttempt())
            assertArrayEquals(byteArrayOf(6,2,4),original.readBytes());sql(store) { assertEquals(7,it.version) }
            val reader=reader(store,source);try { assertEquals(11L,reader.capture().head.sequence) } finally { reader.close() }
        } finally { store.close() }
    }

    @Test fun controllerPolicyAndControlHeadCannotReuseAGlobalRootSequence() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val journal=ProcessorLibraryControllerJournal(store)
            journal.accept(fixture["publisher"].asString.toByteArray(Charsets.UTF_8))
            source.body=fixture["invalidTransitions"].asJsonArray[0].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8)
            rejected { reader.capture() };assertEquals(9L,journal.current().publisher?.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun retainedControlHeadCannotBecomeAnotherRootPolicyRole() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);source.body=fixture["invalidTransitions"].asJsonArray[0].asJsonObject["envelope"].asString.toByteArray(Charsets.UTF_8)
            val captured=reader.capture();assertEquals(9L,captured.head.sequence)
            rejected { ProcessorLibraryControllerJournal(store).accept(fixture["publisher"].asString.toByteArray(Charsets.UTF_8)) }
            reader.assertCaptured(captured)
        } finally { reader.close();store.close() }
    }

    @Test fun wrongMetadataAndChangedCorpusCannotStartBodies() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store)
            for(bad in listOf(
                DriveWorkspaceMetadata("copy","application/json",1,1,listOf(pin.workspace.folderId),false,null,true),
                DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"image/jpeg",1,1,listOf(pin.workspace.folderId),false,null,true),
                DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"application/json",1,null,listOf(pin.workspace.folderId),false,null,true),
                DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"application/json",1,262145,listOf(pin.workspace.folderId),false,null,true),
                DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"application/json",1,1,listOf("foreign"),false,null,true),
                DriveWorkspaceMetadata(checkNotNull(pin.currentFileId),"application/json",1,1,listOf(pin.workspace.folderId),false,null,false))) {
                source.transform={ bad };rejected { reader.capture() }
            }
            assertEquals(0,source.reads);source.transform={ it };source.drive="actual";reader.capture();val reads=source.reads
            source.drive="other";rejected { reader.capture() };assertEquals(reads,source.reads)
        } finally { reader.close();store.close() }
    }

    @Test fun legacyConnectionCannotInferOrSearchForAControlFile() = runBlocking {
        val store=store();val legacy=LibraryConnectionDescriptor.decode(fixture["legacyCode"].asString);val source=Source().also { it.connectionSha256=legacy.documentSha256 }
        val reader=ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,legacy)
        try {
            approve(store,descriptor=legacy);rejected { reader.capture() };assertEquals(0,source.captures);assertEquals(0,source.reads)
        } finally { reader.close();store.close() }
    }

    @Test fun aKnownDriveVersionCannotAcquireDifferentSignedBytes() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);reader.capture();source.body=head(1);rejected { reader.capture() }
            source.version=2;assertEquals(12L,reader.capture().head.sequence)
        } finally { reader.close();store.close() }
    }

    @Test fun changedAttemptBodyCannotWashItsStateIntegrityMarker() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);val capture=reader.capture()
            sql(store) { db ->
                val body=db.rawQuery("SELECT document FROM current_control_attempt",null).use { row -> assertTrue(row.moveToFirst());JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject }
                body.addProperty("phase","fenced");val bytes=CanonicalProtocolJson(4096).encode(body.toString().toByteArray(Charsets.UTF_8))
                db.execSQL("UPDATE current_control_attempt SET document=?,sha256=?",arrayOf(bytes,ProcessorWorkspaceObservations.hash(bytes)))
            }
            try { reader.assertCaptured(capture);fail("A rewritten attempt bypassed its immutable state marker") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { reader.close();store.close() }
    }

    @Test fun missingEarlierHeadCannotBeWashedByItsStillValidSuccessor() = runBlocking {
        val store=store();val source=Source();val reader=reader(store,source)
        try {
            approve(store);reader.capture();source.body=head(1);source.version=2;val capture=reader.capture()
            sql(store) { it.execSQL("DELETE FROM current_control_head WHERE sequence=11") }
            try { reader.assertCaptured(capture);fail("A missing signed-history prefix was ignored") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { reader.close();store.close() }
    }
}
