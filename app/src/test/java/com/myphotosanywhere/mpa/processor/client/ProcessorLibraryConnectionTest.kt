package com.myphotosanywhere.mpa.processor.client

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.util.concurrent.TimeUnit
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import java.util.Base64
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.awaitCancellation
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
@Config(sdk=[28], application=Application::class)
class ProcessorLibraryConnectionTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private fun descriptor(): LibraryConnectionDescriptor {
        val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-connection.json"))
            .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
        return LibraryConnectionDescriptor.decode(fixture["valid"].asJsonArray[0].asJsonObject["code"].asString)
    }
    private fun changed(value: LibraryConnectionDescriptor) = LibraryConnectionDescriptor(value.libraryId, value.controllerId,
        value.controllerEpoch + 1, value.workspace, Base64.getDecoder().decode(value.controllerPublicKeySpki))
    private class Capacity : StorageBudget.Capacity {
        var bytes = 1024L * 1024 * 1024
        override fun available(directory: File) = bytes
        override fun allocatable(directory: File) = bytes
        override fun allocate(directory: File, bytes: Long) { error("The pin store must not delete originals to obtain space.") }
    }
    private class Reader(value: LibraryConnectionDescriptor) : ProcessorLibraryConnectionReader {
        var account = value.workspace.accountId
        var email = "synthetic@example.invalid"
        var folder = ProcessorLibraryWorkspace(value.workspace.folderId, "Library metadata", 7, true, false)
        var accountReads = 0
        var read: (suspend () -> Unit)? = null
        override suspend fun account(): ProcessorLibraryAccount { accountReads++; return ProcessorLibraryAccount(account,email) }
        override suspend fun workspace(id: String): ProcessorLibraryWorkspace { assertEquals(folder.id, id); read?.invoke(); return folder }
    }
    private fun store(capacity: Capacity = Capacity()) = ProcessorLibraryPinStore(context, StorageBudget(capacity))
    private fun sql(owner: ProcessorLibraryPinStore, command: String) {
        SQLiteDatabase.openDatabase(File(owner.directory,"connection.sqlite").absolutePath, null, SQLiteDatabase.OPEN_READWRITE).use { it.execSQL(command) }
    }

    @Test fun reviewDoesNotPinAndExactApprovalSurvivesRecreation() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); var pins = store(); var stage = "initial review"
        try {
            val client = ProcessorLibraryConnection(pins, reader); val review = client.review(descriptor.encode())
            assertNull(pins.current()); assertEquals(2, reader.accountReads)
            stage = "initial approval"
            assertEquals(descriptor.encode(), client.approve(review, descriptor.documentSha256).encode()); assertEquals(4, reader.accountReads)
            assertTrue(pins.directory.canonicalPath.startsWith(context.noBackupFilesDir.canonicalPath + File.separator))
            pins.close(); pins = store(); stage = "reopened read"
            assertEquals(descriptor.encode(), pins.current()?.encode())
            val recreated = ProcessorLibraryConnection(pins, reader)
            assertEquals(descriptor.encode(), recreated.approve(recreated.review(descriptor.encode()), descriptor.documentSha256).encode())
            assertFalse(File(context.getDatabasePath("mpa.db").absolutePath).exists())
        } catch (error: Exception) {
            val file = File(pins.directory,"connection.sqlite")
            throw AssertionError("Native SQLite failed during $stage; pathLength=${file.absolutePath.length}; parentExists=${pins.directory.isDirectory}; fileExists=${file.exists()}; path=${file.absolutePath}",error)
        } finally { pins.close() }
    }

    @Test fun selectorSurvivesReopenWithoutBecomingAnAuthority() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); var pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,reader); val review = client.review(descriptor.encode())
            client.approve(review,descriptor.documentSha256); pins.close(); pins = store()
            assertEquals(review.account,pins.registration()?.account)
            assertEquals(descriptor.encode(),pins.registration()?.descriptor?.encode())
            sql(pins,"UPDATE credential_selector SET account_id='another-permission'")
            try { pins.registration(); fail("A selector changed library authority") } catch (_: ProcessorLibraryStateCorrupt) { }
        } finally { pins.close() }
    }

    @Test fun migratedPinKeepsItsMarkerAndNeedsFreshAccountApproval() = runBlocking {
        val descriptor = descriptor(); val pins = store()
        try {
            assertTrue(pins.directory.isDirectory || pins.directory.mkdirs())
            SQLiteDatabase.openOrCreateDatabase(File(pins.directory,"connection.sqlite"),null).use { database ->
                database.execSQL("CREATE TABLE approved_pin(singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton=1),code TEXT NOT NULL CHECK(length(code) BETWEEN 1 AND 4096),document_sha256 TEXT NOT NULL CHECK(length(document_sha256)=64))")
                database.execSQL("CREATE TABLE client_meta(key TEXT NOT NULL PRIMARY KEY,value TEXT NOT NULL)")
                database.execSQL("INSERT INTO approved_pin VALUES(1,?,?)",arrayOf(descriptor.encode(),descriptor.documentSha256))
                database.execSQL("INSERT INTO client_meta VALUES('approved_pin_sha256',?)",arrayOf(descriptor.documentSha256))
                database.version=1
            }
            assertEquals(descriptor.encode(),pins.registration()?.descriptor?.encode()); assertNull(pins.registration()?.account)
            val client = ProcessorLibraryConnection(pins,Reader(descriptor)); client.approve(client.review(descriptor.encode()),descriptor.documentSha256)
            assertEquals("synthetic@example.invalid",pins.registration()?.account?.email)
            SQLiteDatabase.openDatabase(File(pins.directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READONLY).use { database ->
                assertEquals(2,database.version)
                database.rawQuery("SELECT value FROM client_meta WHERE key='approved_pin_sha256'",null).use { row -> assertTrue(row.moveToFirst()); assertEquals(descriptor.documentSha256,row.getString(0)) }
            }
        } finally { pins.close() }
    }

    @Test fun failedSelectorUpdatePreservesPinAndEarlierAccount() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); val pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,reader); client.approve(client.review(descriptor.encode()),descriptor.documentSha256)
            reader.email="renamed@example.invalid"; val review = client.review(descriptor.encode())
            sql(pins,"CREATE TRIGGER reject_selector BEFORE UPDATE ON credential_selector BEGIN SELECT RAISE(ABORT,'synthetic selector failure'); END")
            try { client.approve(review,descriptor.documentSha256); fail("Failed selector commit was acknowledged") } catch (_: SQLiteException) { }
            assertEquals("synthetic@example.invalid",pins.registration()?.account?.email); assertEquals(descriptor.encode(),pins.current()?.encode())
            sql(pins,"DROP TRIGGER reject_selector"); client.approve(review,descriptor.documentSha256)
            assertEquals(reader.email,pins.registration()?.account?.email); assertEquals(descriptor.encode(),pins.current()?.encode())
        } finally { pins.close() }
    }

    @Test fun changedEmailCannotApproveAnEarlierAccountReview() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); val pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,reader); val review = client.review(descriptor.encode())
            reader.email="different@example.invalid"
            try { client.approve(review,descriptor.documentSha256); fail("Changed credential selector was accepted") } catch (_: IOException) { }
            assertNull(pins.registration())
            val refreshed = client.review(descriptor.encode()); client.approve(refreshed,descriptor.documentSha256)
            assertEquals(reader.email,pins.registration()?.account?.email)
        } finally { pins.close() }
    }

    @Test fun actualDriveAdapterReadsOnlyAccountAndFolderMetadata() = runBlocking {
        val descriptor = descriptor(); val pins = store()
        val server = MockWebServer().apply { start(InetAddress.getByName("127.0.0.1"),0) }
        try {
            val user = """{"user":{"permissionId":"${descriptor.workspace.accountId}","emailAddress":"synthetic@example.invalid"}}"""
            val folder = """{"id":"${descriptor.workspace.folderId}","name":"Library metadata","version":"7","mimeType":"application/vnd.google-apps.folder","trashed":false}"""
            repeat(2) { server.enqueue(MockResponse().setBody(user)); server.enqueue(MockResponse().setBody(folder)); server.enqueue(MockResponse().setBody(user)) }
            val gateway = DriveGateway("synthetic-access",OkHttpClient(),server.url("/drive/v3/"),server.url("/upload/drive/v3/files"))
            val client = ProcessorLibraryConnection(pins,ProcessorLibraryDriveReader(gateway))
            val review = client.review(descriptor.encode()); assertNull(pins.current())
            assertEquals(descriptor.encode(),client.approve(review,descriptor.documentSha256).encode())
            assertEquals(6,server.requestCount)
            repeat(6) { index ->
                val request = checkNotNull(server.takeRequest(5,TimeUnit.SECONDS))
                assertEquals("GET",request.method); assertEquals("Bearer synthetic-access",request.getHeader("Authorization"))
                val expected = if (index % 3 == 1) "/drive/v3/files/${descriptor.workspace.folderId}" else "/drive/v3/about"
                assertEquals(expected,request.requestUrl?.encodedPath)
                assertNull(request.requestUrl?.queryParameter("alt")); assertEquals(0L,request.bodySize)
            }
        } finally { server.shutdown(); pins.close() }
    }

    @Test fun accountWorkspaceAndReviewedDigestMustStillMatchBeforeApproval() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); val pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,reader)
            reader.account = "another-permission"
            try { client.review(descriptor.encode()); fail("Different account was accepted") } catch (_: IOException) { }
            assertNull(pins.current()); reader.account = descriptor.workspace.accountId
            reader.read = { reader.account = "changed-during-read" }
            try { client.review(descriptor.encode()); fail("Changed account was accepted") } catch (_: IOException) { }
            reader.read = null; reader.account = descriptor.workspace.accountId
            val review = client.review(descriptor.encode()); val calls = reader.accountReads
            try { client.approve(review, "0".repeat(64)); fail("Unapproved digest was accepted") } catch (_: IOException) { }
            assertEquals(calls, reader.accountReads); reader.folder = reader.folder.copy(version=8)
            try { client.approve(review,descriptor.documentSha256); fail("Changed workspace was accepted") } catch (_: IOException) { }
            assertNull(pins.current()); reader.folder = reader.folder.copy(version=7)
            client.approve(review,descriptor.documentSha256)
            try { client.review(changed(descriptor).encode()); fail("A different root epoch replaced accepted trust") } catch (_: ProcessorLibraryPinConflict) { }
            assertEquals(descriptor.encode(),pins.current()?.encode())
        } finally { pins.close() }
    }

    @Test fun missingTrashedAndNonfolderWorkspacesNeverPinAConnection() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); val pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,reader); val original = reader.folder
            for (folder in listOf(original.copy(id="elsewhere"), original.copy(folder=false), original.copy(trashed=true), original.copy(version=0))) {
                reader.folder = folder
                // An actual adapter can return a wrong ID; it must be rejected rather than followed.
                val adapter = object : ProcessorLibraryConnectionReader {
                    override suspend fun account() = ProcessorLibraryAccount(descriptor.workspace.accountId,"synthetic@example.invalid")
                    override suspend fun workspace(id: String) = folder
                }
                try { ProcessorLibraryConnection(pins,adapter).review(descriptor.encode()); fail("Invalid folder was accepted") } catch (_: IOException) { }
                assertNull(pins.current())
            }
            reader.folder = original
            assertEquals(descriptor.encode(),client.review(descriptor.encode()).descriptor.encode())
        } finally { pins.close() }
    }

    @Test fun cancellationJoinsTheActualAccountWorkspaceReadWithoutCommittingApproval() = runBlocking {
        val descriptor = descriptor(); val reader = Reader(descriptor); val pins = store()
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        try {
            reader.read = { entered.complete(Unit); withContext(NonCancellable) { release.await() } }
            supervisorScope {
                val reading = async { ProcessorLibraryConnection(pins,reader).review(descriptor.encode()) }
                try {
                    withTimeout(5000) { entered.await() }; reading.cancel(); assertFalse(reading.isCompleted)
                } finally {
                    reading.cancel(); release.complete(Unit)
                    try { reading.await(); fail("Canceled review completed") } catch (_: CancellationException) { }
                }
            }
            assertNull(pins.current())
        } finally { release.complete(Unit); pins.close() }
    }

    @Test fun aFailedAtomicApprovalKeepsBothPinAndMarkerUnchangedAndRetriesTheSameReview() = runBlocking {
        val descriptor = descriptor(); val pins = store(); val client = ProcessorLibraryConnection(pins,Reader(descriptor))
        try {
            val review = client.review(descriptor.encode())
            sql(pins,"CREATE TRIGGER reject_marker BEFORE INSERT ON client_meta BEGIN SELECT RAISE(ABORT,'synthetic pin commit failure'); END")
            try { client.approve(review,descriptor.documentSha256); fail("Failed commit was acknowledged") } catch (_: SQLiteException) { }
            assertNull(pins.current())
            sql(pins,"DROP TRIGGER reject_marker")
            assertEquals(descriptor.encode(),client.approve(review,descriptor.documentSha256).encode())
        } finally { pins.close() }
    }

    @Test fun lowStoragePreservesApprovalAndAnExistingPinRemainsReadableAfterReopen() = runBlocking {
        val descriptor = descriptor(); val capacity = Capacity(); var pins = store(capacity)
        try {
            val client = ProcessorLibraryConnection(pins,Reader(descriptor)); val review = client.review(descriptor.encode())
            capacity.bytes = 1
            try { client.approve(review,descriptor.documentSha256); fail("Low storage commit was acknowledged") } catch (_: ProcessorClientStorageUnavailable) { }
            assertNull(pins.current()); capacity.bytes = 1024L * 1024 * 1024
            client.approve(review,descriptor.documentSha256); pins.close(); capacity.bytes = 1; pins = store(capacity)
            assertEquals(descriptor.encode(),pins.current()?.encode())
        } finally { pins.close() }
    }

    @Test fun aMissingAcceptedPinCannotBeRecreatedFromAnotherValidCode() = runBlocking {
        val descriptor = descriptor(); val pins = store()
        try {
            val client = ProcessorLibraryConnection(pins,Reader(descriptor)); val review = client.review(descriptor.encode())
            client.approve(review,descriptor.documentSha256); sql(pins,"DELETE FROM approved_pin")
            try { pins.current(); fail("Missing accepted pin was treated as fresh setup") } catch (_: IllegalStateException) { }
            try { client.approve(review,descriptor.documentSha256); fail("Missing accepted pin was recreated") } catch (_: IllegalStateException) { }
        } finally { pins.close() }
    }

    @Test fun closeFromACanceledCallerStillJoinsAndFencesItsActualSqliteOwner() = runBlocking {
        val pins = store(); val entered = CompletableDeferred<Unit>()
        try {
            pins.current()
            supervisorScope {
                val owner = async {
                    try { entered.complete(Unit); awaitCancellation() }
                    finally { pins.close() }
                }
                try { withTimeout(5000) { entered.await() } }
                finally {
                    owner.cancel()
                    try { owner.await(); fail("Canceled owner completed") } catch (_: CancellationException) { }
                }
            }
            try { pins.current(); fail("Canceled cleanup retained an open owner") } catch (_: IllegalStateException) { }
        } finally { pins.close() }
    }

    @Test fun twoActualSqliteOwnersCannotSilentlyReplaceEachOthersApprovedRoot() = runBlocking {
        val descriptor = descriptor(); val first = store(); val second = store()
        try {
            val reader = Reader(descriptor)
            val reviewA = ProcessorLibraryConnection(first,reader).review(descriptor.encode())
            val reviewB = ProcessorLibraryConnection(second,reader).review(changed(descriptor).encode())
            val results = supervisorScope { listOf(first to reviewA, second to reviewB).map { (pins,review) -> async {
                try { pins.approve(review).encode() } catch (_: ProcessorLibraryPinConflict) { null }
            } }.awaitAll() }
            assertEquals(1,results.count { it != null }); val winner = results.single { it != null }
            assertEquals(winner,first.current()?.encode()); assertEquals(winner,second.current()?.encode())
        } finally { first.close(); second.close() }
    }
}
