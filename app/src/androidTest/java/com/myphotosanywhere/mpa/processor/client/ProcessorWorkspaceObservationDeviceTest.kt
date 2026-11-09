package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChange
import com.myphotosanywhere.mpa.drive.DriveWorkspaceChangesPage
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.io.IOException
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorWorkspaceObservationDeviceTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("test-account","test-workspace"),key.public.encoded)
    private fun directory() = File(context.noBackupFilesDir,"workspace-test-${UUID.randomUUID()}")
    private fun store(directory: File) = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
    private fun sql(directory: File,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private suspend fun approve(store: ProcessorLibraryStateStore,descriptor: LibraryConnectionDescriptor = pin) {
        val connection = ProcessorLibraryConnection(store,object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount("test-account","synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        });connection.approve(connection.review(descriptor.encode()),descriptor.documentSha256)
    }
    private fun cleanup(directory: File) {
        check(directory.parentFile?.canonicalFile == context.noBackupFilesDir.canonicalFile && directory.name.startsWith("workspace-test-"))
        check(!directory.exists() || directory.deleteRecursively())
    }
    private fun metadata(id: String) = DriveWorkspaceMetadata(id,"application/json",1,2,listOf("test-workspace"),false,null,true)
    private fun source(ids: List<String>) = object : ProcessorWorkspaceInventorySource {
        override val connectionSha256 = pin.documentSha256
        override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor")
        override suspend fun page(token: String?) = DriveWorkspacePage(ids.map(::metadata),null,false)
        override suspend fun readCaptured(expected: DriveWorkspaceMetadata) = "{}".toByteArray()
    }

    @Test fun nativeFailureKeepsUnreadIdentityAcrossReopen() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);var observations = ProcessorWorkspaceObservations(store)
            try { ProcessorWorkspaceInventory(observations,source(listOf("a","b"))).prepare { _,_ -> throw IOException("Synthetic signature failure") };fail("Failed verification became successful") } catch (_: IOException) { }
            val previous = checkNotNull(observations.lastAttempt());assertTrue(previous.inventoryListed)
            sql(directory) { db -> db.rawQuery("SELECT count(*) FROM workspace_candidate",null).use { assertTrue(it.moveToFirst());assertEquals(2,it.getInt(0)) } }
            store.close();store = store(directory);observations = ProcessorWorkspaceObservations(store)
            assertEquals(previous,observations.lastAttempt())
            try { ProcessorWorkspaceInventory(observations,source(listOf("a"))).prepare { _,_ -> fail("Forgot unread metadata") };fail("Missing identity became success") } catch (_: IOException) { }
            assertTrue(checkNotNull(observations.lastAttempt()).revision > previous.revision)
            assertEquals(previous.firstUnresolved,observations.lastAttempt()?.firstUnresolved)
            try { observations.reads(previous);fail("Old owner remained current") } catch (_: ProcessorWorkspaceConflict) { }
        } finally { store.close();cleanup(directory) }
    }

    @Test fun nativeSchemaThreeKeepsOriginalAndApprovedAccount() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);val original = File(directory,"original.jpg").apply { writeBytes(byteArrayOf(7,5,3,1)) };store.close()
            sql(directory) { db -> db.execSQL("DROP TABLE current_control_attempt");db.execSQL("DROP TABLE current_control_head");db.execSQL("DROP TABLE workspace_history");db.execSQL("DROP TABLE workspace_replay");db.execSQL("DROP TABLE workspace_event");db.execSQL("DROP TABLE workspace_read");db.execSQL("DROP TABLE workspace_candidate");db.execSQL("DROP TABLE workspace_attempt");db.version = 3 }
            store = store(directory);assertEquals(pin.encode(),store.current()?.encode());assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            val prepared = ProcessorWorkspaceInventory(ProcessorWorkspaceObservations(store),source(listOf("a"))).prepare { _,_ -> }
            assertEquals(1,prepared.reads.size);assertArrayEquals(byteArrayOf(7,5,3,1),original.readBytes())
            sql(directory) { assertEquals(7,it.version) }
        } finally { store.close();cleanup(directory) }
    }
    @Test fun nativeRemovedHistoryPersistsAndCannotBeWashedAfterRestart() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);var observations = ProcessorWorkspaceObservations(store)
            val ready = ProcessorWorkspaceInventory(observations,source(listOf("a"))).prepare { _,_ -> }
            val changes = object : ProcessorWorkspaceHistorySource {
                override suspend fun changes(cursor: DriveWorkspaceCursor) = DriveWorkspaceChangesPage(listOf(DriveWorkspaceChange("file","removed",true,null)),null,cursor.copy(token="after"))
            }
            try { ProcessorWorkspaceHistory(observations,changes).replay(ready);fail("Removal became complete") } catch (_: ProcessorWorkspaceHistoryRecovery) { }
            val failed = checkNotNull(observations.lastAttempt());store.close();store = store(directory);observations = ProcessorWorkspaceObservations(store)
            assertEquals(failed,observations.lastAttempt());val next = ProcessorWorkspaceInventory(observations,source(listOf("a"))).prepare { _,_ -> }
            val forbidden = object : ProcessorWorkspaceHistorySource { override suspend fun changes(cursor: DriveWorkspaceCursor): DriveWorkspaceChangesPage = error("Sticky removal must survive inventory success") }
            try { ProcessorWorkspaceHistory(observations,forbidden).replay(next);fail("Removal was washed away") } catch (_: ProcessorWorkspaceHistoryRecovery) { }
            assertEquals("cursor",observations.lastAttempt()?.firstUnresolved)
        } finally { store.close();cleanup(directory) }
    }
    @Test fun nativeFinalFenceDoesNotAdvancePastRejectedCandidate() = runBlocking<Unit> {
        val directory = directory();val store = store(directory)
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val ready = ProcessorWorkspaceInventory(observations,source(listOf("a"))).prepare { _,_ -> }
            val clean = object : ProcessorWorkspaceHistorySource { override suspend fun changes(cursor: DriveWorkspaceCursor) = DriveWorkspaceChangesPage(emptyList(),null,cursor.copy(token="end")) }
            val batch = ProcessorWorkspaceHistory(observations,clean).replay(ready)
            val changed = object : ProcessorWorkspaceHistorySource { override suspend fun changes(cursor: DriveWorkspaceCursor) = DriveWorkspaceChangesPage(listOf(DriveWorkspaceChange("file","new",false,metadata("new"))),null,cursor.copy(token="must-not-advance")) }
            try { ProcessorWorkspaceHistory(observations,changed).checkUnchanged(batch,ready.reads);fail("Changed final fence was accepted") } catch (_: ProcessorWorkspaceConflict) { }
            sql(directory) { db ->
                db.rawQuery("SELECT document FROM workspace_replay",null).use { row -> assertTrue(row.moveToFirst());val value = com.google.gson.JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject;assertEquals("end",value["cursor"].asJsonObject["token"].asString);assertFalse(value["end"].asBoolean) }
                db.rawQuery("SELECT count(*) FROM workspace_candidate WHERE file_id='new'",null).use { row -> assertTrue(row.moveToFirst());assertEquals(1,row.getInt(0)) }
            }
        } finally { store.close();cleanup(directory) }
    }

    @Test fun nativeSchemaFourPreservesExactPendingJournalAndGlobalScope() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val ready = ProcessorWorkspaceInventory(observations,source(listOf("a","b"))).prepare { _,_ -> }
            val original = File(directory,"original.jpg").apply { writeBytes(byteArrayOf(9,7,5,3)) }
            var events = listOf<ByteArray>()
            sql(directory) { db ->
                db.rawQuery("SELECT document FROM workspace_attempt WHERE singleton=1",null).use { row ->
                    assertTrue(row.moveToFirst());val legacy = com.google.gson.JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject
                    listOf("startDrive","unresolvedDrive","history","replay").forEach { legacy.remove(it) }
                    val bytes = com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson(65536).encode(legacy.toString().toByteArray(Charsets.UTF_8))
                    db.execSQL("UPDATE workspace_attempt SET document=?,sha256=?",arrayOf(bytes,ProcessorWorkspaceObservations.hash(bytes)))
                }
                db.rawQuery("SELECT document FROM workspace_event ORDER BY sequence",null).use { rows -> val values = arrayListOf<ByteArray>();while(rows.moveToNext()) values.add(rows.getBlob(0));events = values }
                db.execSQL("DROP TABLE current_control_attempt");db.execSQL("DROP TABLE current_control_head");db.execSQL("DROP TABLE workspace_history");db.execSQL("DROP TABLE workspace_replay");db.execSQL("DROP INDEX workspace_event_lookup");db.execSQL("ALTER TABLE workspace_event RENAME TO old_events")
                db.execSQL("CREATE TABLE workspace_event(sequence INTEGER NOT NULL PRIMARY KEY CHECK(sequence>0),kind TEXT NOT NULL CHECK(kind IN ('candidate','read')),file_id TEXT NOT NULL CHECK(length(file_id) BETWEEN 1 AND 256),document BLOB NOT NULL CHECK(length(document) BETWEEN 1 AND 2048),sha256 TEXT NOT NULL CHECK(length(sha256)=64))")
                db.execSQL("CREATE INDEX workspace_event_lookup ON workspace_event(kind,file_id,sequence)");db.execSQL("INSERT INTO workspace_event SELECT * FROM old_events");db.execSQL("DROP TABLE old_events");db.version = 4
            }
            store.close();store = store(directory);val restored = ProcessorWorkspaceObservations(store)
            assertEquals(ready.attempt,restored.lastAttempt());assertEquals(ready.reads,restored.reads(ready.attempt));assertNull(restored.lastAttempt()?.unresolvedDriveId)
            assertEquals(pin.encode(),store.current()?.encode());assertEquals("synthetic@example.invalid",store.registration()?.account?.email);assertArrayEquals(byteArrayOf(9,7,5,3),original.readBytes())
            sql(directory) { db -> assertEquals(7,db.version);db.rawQuery("SELECT document FROM workspace_event ORDER BY sequence",null).use { rows -> var index = 0;while(rows.moveToNext()) assertArrayEquals(events[index++],rows.getBlob(0));assertEquals(events.size,index) } }
        } finally { store.close();cleanup(directory) }
    }

    @Test fun nativeFenceRetiresCatalogTransport() = runBlocking<Unit> {
        val directory = directory();val store = store(directory)
        try {
            approve(store);val observations = ProcessorWorkspaceObservations(store);var captures = 0
            val source = object : ProcessorWorkspaceCapturedSource,ProcessorWorkspaceHistorySource {
                override val connectionSha256 = pin.documentSha256
                override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"cursor")
                override suspend fun page(token: String?) = DriveWorkspacePage(listOf(metadata("a")),null,false)
                override suspend fun capture(fileId: String): DriveWorkspaceMetadata { captures++;return metadata(fileId) }
                override suspend fun readCaptured(expected: DriveWorkspaceMetadata) = "{}".toByteArray()
                override suspend fun changes(cursor: DriveWorkspaceCursor) = DriveWorkspaceChangesPage(emptyList(),null,cursor.copy(token="end"))
            }
            val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> };val batch = ProcessorWorkspaceHistory(observations,source).replay(prepared)
            val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            val reference = com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference(pin.workspace.accountId,"a",pin.workspace.folderId,2,prepared.reads.single().sha256)
            assertArrayEquals("{}".toByteArray(),objects.read(reference));assertEquals(1,captures)
            val fenced = ProcessorWorkspaceHistory(observations,source).checkUnchanged(batch,prepared.reads);assertTrue(fenced.state.fenced)
            try { objects.read(reference);fail("Native transport outlived its final fence") } catch (_: ProcessorWorkspaceConflict) { }
            try { ProcessorCatalogObservedObjects.create(observations,source,fenced);fail("Native final fence reopened a catalog reader") } catch (_: ProcessorWorkspaceConflict) { }
            assertEquals(1,captures)
        } finally { store.close();cleanup(directory) }
    }
    @Test fun nativeSchemaFiveKeepsApprovalAndOriginal() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);val original = File(directory,"original.jpg").apply { writeBytes(byteArrayOf(6,4,2)) };store.close()
            sql(directory) { it.execSQL("DROP TABLE current_control_attempt");it.execSQL("DROP TABLE current_control_head");it.version = 5 }
            store = store(directory);assertEquals(pin.encode(),store.current()?.encode());assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            assertArrayEquals(byteArrayOf(6,4,2),original.readBytes());assertNull(ProcessorWorkspaceObservations(store).lastAttempt())
            sql(directory) { assertEquals(7,it.version) }
        } finally { store.close();cleanup(directory) }
    }


    private fun controlEnvelope(descriptor: LibraryConnectionDescriptor,policies: Map<String,ByteArray>? = null): ByteArray {
        fun reference(file: String): String {
            val bytes=policies?.get(file)
            val size=bytes?.size?.toString() ?: "1";val digest=bytes?.let { ProcessorWorkspaceObservations.hash(it) } ?: "a".repeat(64)
            return """{"accountId":"test-account","workspaceId":"test-workspace","bytes":"$size","sha256":"$digest","fileId":"$file"}"""
        }
        val raw = """{"format":"mpa","major":1,"minor":0,"libraryId":"${descriptor.libraryId}","actorId":"${descriptor.controllerId}","epoch":"1","sequence":"3","kind":"library-current-control","requiredCapabilities":["library.current.v1"],"payload":{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"currentFileId":"control","publishers":{"object":${reference("publisher")},"sequence":"1"},"sources":{"object":${reference("sources")},"sequence":"2"},"catalog":null}}"""
        val body = com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson().encode(raw.toByteArray(Charsets.UTF_8))
        val signer = com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner(key.private,key.public)
        return com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments(setOf("library.current.v1")).encode(body,signer.sign(body))
    }
    @Test fun nativeControlCaptureFencesAndRequiresFreshOwnerAfterReopen() = runBlocking<Unit> {
        val directory=directory();var store=store(directory)
        val descriptor=LibraryConnectionDescriptor(pin.libraryId,pin.controllerId,pin.controllerEpoch,pin.workspace,key.public.encoded,"control")
        val bytes=controlEnvelope(descriptor)
        val source=object : ProcessorWorkspaceCapturedSource {
            override val connectionSha256=descriptor.documentSha256
            override suspend fun capture(fileId: String)=DriveWorkspaceMetadata(fileId,"application/json",1,bytes.size.toLong(),listOf("test-workspace"),false,null,true)
            override suspend fun readCaptured(expected: DriveWorkspaceMetadata)=bytes
            override suspend fun cursor(): DriveWorkspaceCursor=error("Current control must not enumerate account history")
            override suspend fun page(token: String?): DriveWorkspacePage=error("Current control must not search for copies")
        }
        var reader=ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,descriptor)
        try {
            approve(store,descriptor);val old=reader.capture();assertEquals(3L,old.head.sequence);assertEquals("fenced",reader.fence(old).phase)
            try { reader.fence(old);fail("Native fenced control reopened") } catch (_: ProcessorWorkspaceConflict) { }
            reader.close();store.close();store=store(directory);reader=ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,descriptor)
            try { reader.fence(old);fail("Native restarted owner adopted an old capture") } catch (_: ProcessorWorkspaceConflict) { }
            val fresh=reader.capture();assertTrue(fresh.attempt.revision>old.attempt.revision);assertEquals("fenced",reader.fence(fresh).phase)
        } finally { reader.close();store.close();cleanup(directory) }
    }
    @Test fun nativeSchemaSixPreservesPendingEvidenceAndOriginal() = runBlocking<Unit> {
        val directory=directory();var store=store(directory)
        try {
            approve(store);val pending=ProcessorWorkspaceObservations(store).begin(pin.documentSha256)
            val original=File(directory,"original.jpg").apply { writeBytes(byteArrayOf(7,5,3)) };store.close()
            sql(directory) { it.execSQL("DROP TABLE current_control_attempt");it.execSQL("DROP TABLE current_control_head");it.version=6 }
            store=store(directory);assertEquals(pin.encode(),store.current()?.encode());assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            assertEquals(pending,ProcessorWorkspaceObservations(store).lastAttempt());assertArrayEquals(byteArrayOf(7,5,3),original.readBytes())
            sql(directory) { assertEquals(7,it.version) }
        } finally { store.close();cleanup(directory) }
    }

    @Test fun nativeSelectedPoliciesUseTheSameCapturedOwnerAndRetireAtItsFinalFence() = runBlocking<Unit> {
        val directory=directory();val store=store(directory)
        val descriptor=LibraryConnectionDescriptor(pin.libraryId,pin.controllerId,pin.controllerEpoch,pin.workspace,key.public.encoded,"control")
        fun policy(sequence:Long,source:Boolean):ByteArray {
            val kind=if(source) "library-source-configuration" else "library-publisher-authorization"
            val capability=if(source) "library.sources.v1" else "library.publishers.v1"
            val payload=if(source) """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"configurationRevision":"1","accounts":["test-account"],"sources":[]}"""
                else """{"workspace":{"accountId":"test-account","folderId":"test-workspace"},"publisher":null}"""
            val raw="""{"format":"mpa","major":1,"minor":0,"libraryId":"${descriptor.libraryId}","actorId":"${descriptor.controllerId}","epoch":"1","sequence":"$sequence","kind":"$kind","requiredCapabilities":["$capability"],"payload":$payload}"""
            val body=com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson().encode(raw.toByteArray(Charsets.UTF_8))
            val signer=com.myphotosanywhere.mpa.processor.protocol.ProtocolDocumentSigner(key.private,key.public)
            return com.myphotosanywhere.mpa.processor.protocol.SignedProtocolDocuments(setOf(capability)).encode(body,signer.sign(body))
        }
        val bodies=mapOf("publisher" to policy(1,false),"sources" to policy(2,true)).toMutableMap()
        bodies["control"]=controlEnvelope(descriptor,bodies);val readIds=mutableListOf<String>()
        val source=object:ProcessorWorkspaceCapturedSource {
            override val connectionSha256=descriptor.documentSha256
            override suspend fun capture(fileId:String)=DriveWorkspaceMetadata(fileId,"application/json",1,checkNotNull(bodies[fileId]).size.toLong(),listOf("test-workspace"),false,null,true)
            override suspend fun readCaptured(expected:DriveWorkspaceMetadata):ByteArray { readIds+=expected.id;return checkNotNull(bodies[expected.id]) }
            override suspend fun cursor():DriveWorkspaceCursor=error("Selected policies cannot enumerate account history")
            override suspend fun page(token:String?):DriveWorkspacePage=error("Selected policies cannot search for copies")
        }
        val reader=ProcessorCurrentControlReader(ProcessorCurrentControlObservations(store),source,descriptor)
        try {
            approve(store,descriptor);val captured=reader.capture();val policies=reader.readSelectedPolicies(captured)
            assertNull(policies.publishers.publisher);assertEquals(1L,policies.publishers.sequence);assertEquals(1L,policies.sources.revision)
            assertEquals(listOf("control","publisher","sources"),readIds)
            val copied=policies.sourceEnvelope();copied[0]=0;assertArrayEquals(checkNotNull(bodies["sources"]),policies.sourceEnvelope())
            assertEquals("fenced",reader.fence(captured).phase);val count=readIds.size
            try { reader.readSelectedPolicies(captured);fail("Fenced native policies reopened") } catch(_:ProcessorWorkspaceConflict) { }
            assertEquals(count,readIds.size)
        } finally { reader.close();store.close();cleanup(directory) }
    }

}
