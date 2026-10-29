package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.processor.protocol.*
import java.io.File
import java.io.IOException
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.toList
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
class ProcessorCatalogObjectsTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val keys = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839"),UUID.randomUUID(),1,CatalogWorkspace("workspace-account","workspace-folder"),keys.public.encoded)
    private class Capacity : StorageBudget.Capacity {
        override fun available(directory: File) = 1024L * 1024 * 1024
        override fun allocatable(directory: File) = available(directory)
        override fun allocate(directory: File,bytes: Long) { error("Catalog reads must never evict originals.") }
    }
    private fun store() = ProcessorLibraryStateStore(context,StorageBudget(Capacity()))
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val connection = ProcessorLibraryConnection(store,object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount(pin.workspace.accountId,"synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        });connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private inner class Source : ProcessorWorkspaceCapturedSource, ProcessorWorkspaceHistorySource {
        override var connectionSha256 = pin.documentSha256
        var version = 1L;var body = "{}".toByteArray();var captureCount = 0;var bodyCount = 0;var changeCount = 0
        var beforeCapture: (suspend ()->Unit)? = null;var beforeBody: (suspend ()->Unit)? = null
        fun metadata(id: String = "meta") = DriveWorkspaceMetadata(id,"application/json",version,2,listOf(pin.workspace.folderId),false,null,true)
        override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"start")
        override suspend fun page(token: String?) = DriveWorkspacePage(listOf(metadata()),null,false)
        override suspend fun capture(fileId: String): DriveWorkspaceMetadata { captureCount++;beforeCapture?.invoke();return metadata(fileId) }
        override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray { bodyCount++;beforeBody?.invoke();return body }
        override suspend fun changes(cursor: DriveWorkspaceCursor): DriveWorkspaceChangesPage { changeCount++;return DriveWorkspaceChangesPage(emptyList(),null,cursor.copy(token="end-$changeCount")) }
    }
    private suspend fun ready(store: ProcessorLibraryStateStore,source: Source): Pair<ProcessorWorkspaceObservations,ProcessorWorkspaceReplayBatch> {
        approve(store);val observations = ProcessorWorkspaceObservations(store)
        val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> }
        val batch = ProcessorWorkspaceHistory(observations,source).replay(prepared);source.captureCount = 0;source.bodyCount = 0
        return observations to batch
    }
    private fun reference(source: Source,id: String = "meta") = CatalogObjectReference(pin.workspace.accountId,id,pin.workspace.folderId,2,ProcessorWorkspaceObservations.hash(source.body))
    private suspend fun rejects(action: suspend ()->Unit) { try { action();fail("Unverified catalog metadata escaped its retained observation") } catch (_: IOException) { } }
    private fun sql(store: ProcessorLibraryStateStore,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(store.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)

    @Test fun knownReadKeepsExactReceiptAndOwnedBytes() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source)
            val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            val bytes = objects.read(reference(source));assertArrayEquals(source.body,bytes);assertNotSame(source.body,bytes)
            bytes.fill(0);assertArrayEquals("{}".toByteArray(),source.body);assertEquals(1,source.captureCount);assertEquals(1,source.bodyCount)
            assertEquals(batch.attempt,observations.lastAttempt());assertEquals(1,observations.reads(batch.attempt).size)
        } finally { store.close() }
    }
    @Test fun foreignOrEqualCopyNeverStartsTransport() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch);val ref = reference(source)
            for(value in listOf(ref.copy(fileId="equal-copy"),ref.copy(accountId="foreign"),ref.copy(workspaceId="foreign"),ref.copy(bytes=3),ref.copy(sha256="0".repeat(64)))) rejects { objects.read(value) }
            assertEquals(0,source.captureCount);assertEquals(0,source.bodyCount)
            source.connectionSha256 = "0".repeat(64);rejects { ProcessorCatalogObservedObjects.create(observations,source,batch) };rejects { objects.read(ref) };assertEquals(0,source.captureCount)
        } finally { store.close() }
    }
    @Test fun changedVersionFailsBeforeBodyRead() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            source.version = 2;rejects { objects.read(reference(source)) };assertEquals(1,source.captureCount);assertEquals(0,source.bodyCount);assertEquals(batch.attempt,observations.lastAttempt())
        } finally { store.close() }
    }
    @Test fun changedBytesCannotOverwriteStoredReceipt() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val ref = reference(source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            source.body = "[]".toByteArray();rejects { objects.read(ref) };assertEquals(1,source.bodyCount)
            assertEquals(ref.sha256,observations.reads(batch.attempt).single().sha256);assertEquals(batch.attempt,observations.lastAttempt())
        } finally { store.close() }
    }
    @Test fun finalFenceRetiresEveryMetadataRead() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            val fenced = ProcessorWorkspaceHistory(observations,source).checkUnchanged(batch,observations.reads(batch.attempt));assertTrue(fenced.state.fenced)
            rejects { objects.read(reference(source)) };rejects { ProcessorCatalogObservedObjects.create(observations,source,fenced) }
            rejects { ProcessorWorkspaceHistory(observations,source).checkUnchanged(fenced,observations.reads(fenced.attempt)) }
            assertEquals(0,source.captureCount);assertEquals(0,source.bodyCount);assertEquals(2,source.changeCount)
        } finally { store.close() }
    }
    @Test fun pendingOrStickyRecoveryCannotRead() = runBlocking {
        val store = store();try {
            val source = Source();approve(store);val observations = ProcessorWorkspaceObservations(store)
            val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> };var active = observations.startReplay(prepared.attempt)
            rejects { ProcessorCatalogObservedObjects.create(observations,source,active) }
            val event = ProcessorWorkspaceHistoryEvent("file","removed",true,"1".repeat(64))
            active = observations.rememberHistoryPage(active,ProcessorWorkspaceHistoryEvidence(active.state.cursor,active.state.cursor.copy(token="end"),true,listOf(event),emptyList(),false),false)
            rejects { ProcessorCatalogObservedObjects.create(observations,source,active) }
            rejects { ProcessorWorkspaceHistory(observations,source).checkUnchanged(active,observations.reads(active.attempt)) }
            assertEquals(0,source.captureCount);assertEquals(0,source.changeCount)
        } finally { store.close() }
    }
    @Test fun staleCaptureCannotStartTheBodyRead() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            source.beforeCapture = { observations.begin(pin.documentSha256) }
            rejects { objects.read(reference(source)) };assertEquals(1,source.captureCount);assertEquals(0,source.bodyCount)
        } finally { store.close() }
    }
    @Test fun canceledBodyActuallyJoinsBeforeReturn() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            val entered = CompletableDeferred<Unit>();val release = CompletableDeferred<Unit>();var retired = false
            source.beforeBody = { withContext(NonCancellable) { entered.complete(Unit);release.await();retired = true } }
            supervisorScope {
                val owner = async { objects.read(reference(source)) }
                try { withTimeout(10000) { entered.await() };owner.cancel();assertFalse(owner.isCompleted) } finally { release.complete(Unit) }
                try { owner.await();fail("Canceled body read was returned") } catch (_: CancellationException) { }
                assertTrue(retired);assertEquals(batch.attempt,observations.lastAttempt())
            }
        } finally { store.close() }
    }
    @Test fun changedPhaseBytesFailClosed() = runBlocking {
        val store = store();try {
            val source = Source();val (observations,batch) = ready(store,source)
            sql(store) { database ->
                database.rawQuery("SELECT document FROM workspace_replay WHERE file_id='cursor'",null).use { row ->
                    assertTrue(row.moveToFirst());val changed = JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject;changed.addProperty("fenced",true)
                    val bytes = CanonicalProtocolJson(65536).encode(changed.toString().toByteArray(Charsets.UTF_8))
                    database.execSQL("UPDATE workspace_replay SET document=?,sha256=? WHERE file_id='cursor'",arrayOf(bytes,ProcessorWorkspaceObservations.hash(bytes)))
                }
            }
            try { ProcessorCatalogObservedObjects.create(observations,source,batch);fail("Changed phase escaped its immutable event") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { store.close() }
    }
    @Test fun schemaFivePreservesPendingReceipts() = runBlocking {
        var store = store();try {
            val source = Source();val (observations,batch) = ready(store,source);val reads = observations.reads(batch.attempt)
            val original = File(store.directory,"original.jpg").apply { writeBytes(byteArrayOf(9,4,2)) }
            var expected = batch.attempt;var oldEvents = listOf<ByteArray>()
            sql(store) { database ->
                database.beginTransaction()
                try {
                    fun encode(value: com.google.gson.JsonObject) = CanonicalProtocolJson(65536).encode(value.toString().toByteArray(Charsets.UTF_8))
                    val legacyEnd = database.rawQuery("SELECT document FROM workspace_replay WHERE file_id='cursor'",null).use { row -> assertTrue(row.moveToFirst());JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject.apply { remove("fenced") } }
                    val legacyStart = legacyEnd.deepCopy().apply { add("cursor",getAsJsonObject("start").deepCopy());addProperty("total","0");addProperty("batch","0");addProperty("active",true);addProperty("end",false) }
                    val events = database.rawQuery("SELECT sequence,kind,document FROM workspace_event ORDER BY sequence",null).use { rows ->
                        val result = arrayListOf<Triple<Long,String,com.google.gson.JsonObject>>()
                        while(rows.moveToNext()) result.add(Triple(rows.getLong(0),rows.getString(1),JsonParser.parseString(String(rows.getBlob(2),Charsets.UTF_8)).asJsonObject));result
                    }
                    assertEquals(2,events.count { it.second == "replay" })
                    var global = ProcessorWorkspaceObservations.initial(pin.documentSha256,"events")
                    val heads = ProcessorWorkspaceObservations.KINDS.associateWith { ProcessorWorkspaceObservations.initial(pin.documentSha256,it) }.toMutableMap();var replayNumber = 0
                    val encodedEvents = arrayListOf<ByteArray>()
                    for((sequence,kind,event) in events) {
                        if(kind == "replay") { replayNumber++;event.addProperty("documentSha256",ProcessorWorkspaceObservations.hash(encode(if(replayNumber == 1) legacyStart else legacyEnd))) }
                        event.addProperty("previous",global);event.addProperty("previousRole",heads.getValue(kind))
                        val bytes = encode(event);encodedEvents.add(bytes)
                        database.execSQL("UPDATE workspace_event SET document=?,sha256=? WHERE sequence=?",arrayOf(bytes,ProcessorWorkspaceObservations.hash(bytes),sequence))
                        global = ProcessorWorkspaceObservations.eventHash(bytes);heads[kind] = ProcessorWorkspaceObservations.roleHash(bytes)
                    }
                    val replayBytes = encode(legacyEnd);database.execSQL("UPDATE workspace_replay SET document=?,sha256=? WHERE file_id='cursor'",arrayOf(replayBytes,ProcessorWorkspaceObservations.hash(replayBytes)))
                    val attempt = database.rawQuery("SELECT document FROM workspace_attempt WHERE singleton=1",null).use { row -> assertTrue(row.moveToFirst());JsonParser.parseString(String(row.getBlob(0),Charsets.UTF_8)).asJsonObject }
                    attempt.addProperty("eventSha256",global);attempt.addProperty("replay",heads.getValue("replay"))
                    val attemptBytes = encode(attempt);database.execSQL("UPDATE workspace_attempt SET document=?,sha256=? WHERE singleton=1",arrayOf(attemptBytes,ProcessorWorkspaceObservations.hash(attemptBytes)))
                    expected = expected.copy(eventSha256=global,replaySha256=heads.getValue("replay"));oldEvents = encodedEvents
                    database.execSQL("DROP TABLE current_control_attempt");database.execSQL("DROP TABLE current_control_head");database.version = 5;database.setTransactionSuccessful()
                } finally { database.endTransaction() }
            }
            store.close();store = store();val restored = ProcessorWorkspaceObservations(store)
            assertEquals(expected,restored.lastAttempt());assertEquals(reads,restored.reads(expected));assertEquals(pin.encode(),store.current()?.encode())
            assertArrayEquals(byteArrayOf(9,4,2),original.readBytes());assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            sql(store) { database -> assertEquals(7,database.version);database.rawQuery("SELECT document FROM workspace_event ORDER BY sequence",null).use { rows -> var index = 0;while(rows.moveToNext()) assertArrayEquals(oldEvents[index++],rows.getBlob(0));assertEquals(oldEvents.size,index) } }
            val pending = batch.copy(attempt=expected);assertFalse(pending.state.fenced)
            val objects = ProcessorCatalogObservedObjects.create(restored,source,pending);assertArrayEquals(source.body,objects.read(reference(source)))
            val fenced = ProcessorWorkspaceHistory(restored,source).checkUnchanged(pending,reads);assertTrue(fenced.state.fenced);rejects { objects.read(reference(source)) }
        } finally { store.close() }
    }
    @Test fun signedCatalogUsesObservedReadSet() = runBlocking {
        val store = store();try {
            approve(store);val observations = ProcessorWorkspaceObservations(store)
            val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/catalog-covered-traversal.json")).bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
            val graph = fixture.getAsJsonObject("valid");assertEquals(pin.libraryId.toString(),graph["libraryId"].asString)
            val payloads = graph.getAsJsonObject("objects").entrySet().associate { it.key to java.util.Base64.getDecoder().decode(it.value.asString) }.toMutableMap()
            val publisherKeys = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
            val publisher = UUID.randomUUID();val capabilities = graph["requiredCapabilities"].asJsonArray.map { it.asString }.toSet()
            val body = CanonicalProtocolJson().encode(com.google.gson.JsonObject().apply {
                addProperty("format","mpa");addProperty("major",1);addProperty("minor",0);addProperty("libraryId",pin.libraryId.toString());addProperty("actorId",publisher.toString());addProperty("epoch","1");addProperty("sequence",graph["sequence"].asString);addProperty("kind","catalog-commit")
                add("requiredCapabilities",graph["requiredCapabilities"].deepCopy());add("payload",graph["commit"].deepCopy())
            }.toString().toByteArray(Charsets.UTF_8))
            val envelope = SignedProtocolDocuments(capabilities).encode(body,ProtocolDocumentSigner(publisherKeys.private,publisherKeys.public).sign(body));payloads["head"] = envelope
            val graphReads = arrayListOf<String>()
            val source = object : ProcessorWorkspaceCapturedSource,ProcessorWorkspaceHistorySource {
                override val connectionSha256 = pin.documentSha256
                private fun metadata(id: String): DriveWorkspaceMetadata {
                    val bytes = checkNotNull(payloads[id]);val mime = if(bytes.size>2 && bytes[0]==31.toByte() && bytes[1]==139.toByte()) "application/gzip" else "application/json"
                    return DriveWorkspaceMetadata(id,mime,1,bytes.size.toLong(),listOf(pin.workspace.folderId),false,ProcessorWorkspaceObservations.hash(bytes),true)
                }
                override suspend fun cursor() = DriveWorkspaceCursor(pin.workspace.accountId,null,"start")
                override suspend fun page(token: String?) = DriveWorkspacePage(payloads.keys.sorted().map(::metadata),null,false)
                override suspend fun capture(fileId: String) = metadata(fileId)
                override suspend fun readCaptured(expected: DriveWorkspaceMetadata): ByteArray { graphReads.add(expected.id);return checkNotNull(payloads[expected.id]) }
                override suspend fun changes(cursor: DriveWorkspaceCursor) = DriveWorkspaceChangesPage(emptyList(),null,cursor.copy(token="end"))
            }
            val prepared = ProcessorWorkspaceInventory(observations,source).prepare { _,_ -> }
            val batch = ProcessorWorkspaceHistory(observations,source).replay(prepared);graphReads.clear()
            val objects = ProcessorCatalogObservedObjects.create(observations,source,batch)
            val head = CatalogObjectReference(pin.workspace.accountId,"head",pin.workspace.folderId,envelope.size.toLong(),ProcessorWorkspaceObservations.hash(envelope))
            val entries = CatalogCommitHistoryTraversal(pin.workspace,TrustedProtocolActor(pin.libraryId,publisher,1,publisherKeys.public.encoded,setOf("catalog-commit")),objects).read(head).toList()
            assertEquals(1,entries.size)
            var coverageSeen = false
            val leaves = CatalogFactsTraversal(pin.libraryId,pin.workspace,objects,graph["objectBudget"].asInt).readCoveredLeaves(entries.single().verified) { coverage ->
                assertEquals(7L,coverage.root.configurationRevision);assertEquals(3,coverage.sources.size);coverageSeen = true
            }.toList()
            assertTrue(coverageSeen);assertEquals(fixture["expectedIds"].asJsonArray.map { it.asString },leaves.flatMap { it.facts.assets }.map { it.id.toString() })
            assertEquals(listOf("head","coverage-only","coverage-page-0","coverage-page-1","index-root","index-56","leaf-5603","leaf-5687"),graphReads)
            assertEquals(batch.attempt,observations.lastAttempt())
            val final = ProcessorWorkspaceHistory(observations,source).checkUnchanged(batch,prepared.reads);assertTrue(final.state.fenced);rejects { objects.read(head) }
        } finally { store.close() }
    }

}
