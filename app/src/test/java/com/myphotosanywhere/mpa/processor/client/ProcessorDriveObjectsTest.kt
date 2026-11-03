package com.myphotosanywhere.mpa.processor.client

import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.drive.DriveWorkspaceCursor
import com.myphotosanywhere.mpa.drive.DriveGateway
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogObjectReference
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.Closeable
import java.io.IOException
import java.net.InetAddress
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.Assert.*
import org.junit.Test

class ProcessorDriveObjectsTest {
    private fun pin(): LibraryConnectionDescriptor {
        val fixture = checkNotNull(javaClass.classLoader?.getResourceAsStream("v1/library-connection.json"))
            .bufferedReader(Charsets.UTF_8).use { JsonParser.parseReader(it).asJsonObject }
        return LibraryConnectionDescriptor.decode(fixture["valid"].asJsonArray[0].asJsonObject["code"].asString)
    }
    private class Fixture(val pin: LibraryConnectionDescriptor) : Closeable {
        val server = MockWebServer()
        val requests = CopyOnWriteArrayList<RecordedRequest>()
        @Volatile var principal = pin.workspace.accountId
        @Volatile var version = "5"
        @Volatile var mime = "application/json"
        @Volatile var parent = pin.workspace.folderId
        @Volatile var sharedDrive: String? = null
        @Volatile var metadataDrive: String? = null
        @Volatile var history: () -> MockResponse = { MockResponse().setBody("""{"changes":[],"newStartPageToken":"after"}""") }
        @Volatile var afterHistory: () -> Unit = { }
        @Volatile var workspaceMime = DriveGateway.FOLDER_MIME
        @Volatile var metadataSha: String? = null
        @Volatile var pageToken: String? = null
        @Volatile var incomplete = false
        @Volatile var media: () -> MockResponse = { MockResponse().setBody("{}") }
        @Volatile var afterMedia: () -> Unit = { }
        @Volatile var page: (() -> MockResponse)? = null
        val expectedHash = hash("{}".toByteArray())
        @Volatile var downloadable = true
        @Volatile var accountReply: (() -> MockResponse)? = null
        private val client = OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).readTimeout(2,TimeUnit.SECONDS).build()
        val source: ProcessorLibraryDriveObjects
        init {
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    requests.add(request)
                    val url = checkNotNull(request.requestUrl)
                    val value = when {
                        url.encodedPath == "/drive/v3/about" -> { accountReply?.let { return it() }; """{"user":{"permissionId":"$principal","emailAddress":"synthetic@example.invalid"}}""" }
                        url.encodedPath == "/drive/v3/changes" -> return history().also { afterHistory() }
                        url.encodedPath == "/drive/v3/changes/startPageToken" -> """{"startPageToken":"actual_cursor"}"""
                        url.encodedPath == "/drive/v3/files/${pin.workspace.folderId}" -> """{"id":"${pin.workspace.folderId}","mimeType":"$workspaceMime","version":"7","parents":[],"trashed":false${sharedDrive?.let { ",\"driveId\":\"$it\"" } ?: ""}}"""
                        url.encodedPath == "/drive/v3/files/meta" && url.queryParameter("alt") == "media" -> return media().also { afterMedia() }
                        url.encodedPath == "/drive/v3/files/meta" -> metadata()
                        url.encodedPath == "/drive/v3/files" -> {
                            page?.let { return it() }
                            """{"files":[${metadata()}],"incompleteSearch":$incomplete${pageToken?.let { ",\"nextPageToken\":\"$it\"" } ?: ""}}"""
                        }
                        else -> return MockResponse().setResponseCode(404)
                    }
                    return MockResponse().setHeader("Content-Type","application/json").setBody(value)
                }
            }
            server.start(InetAddress.getByName("127.0.0.1"),0)
            val api = server.url("/drive/v3/")
            source = ProcessorLibraryDriveObjects(DriveGateway("synthetic-access",client,api,api),pin)
        }
        fun metadata() = """{"id":"meta","mimeType":"$mime","version":"$version","size":"2","parents":["$parent"],"trashed":false,"capabilities":{"canDownload":$downloadable}${metadataSha?.let { ",\"sha256Checksum\":\"$it\"" } ?: ""}${metadataDrive?.let { ",\"driveId\":\"$it\"" } ?: ""}}"""
        fun reference(hash: String = expectedHash) = CatalogObjectReference(pin.workspace.accountId,"meta",pin.workspace.folderId,2,hash)
        fun downloads() = requests.count { it.requestUrl?.queryParameter("alt") == "media" }
        override fun close() {
            try { server.shutdown() } finally {
                client.connectionPool.evictAll(); client.dispatcher.executorService.shutdown()
                check(client.dispatcher.executorService.awaitTermination(5,TimeUnit.SECONDS)) { "Fixture HTTP dispatcher did not retire." }
            }
            assertTrue(requests.all { it.method == "GET" })
        }
    }
    private suspend fun rejects(action: suspend () -> Unit) {
        try { action(); fail("Unverified workspace read was accepted") } catch (_: IOException) { }
    }

    @Test fun actualMetadataCorpusMustMatchItsCapturedWorkspaceBeforeBody() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.sharedDrive="actual-drive";fixture.metadataDrive="foreign-drive"
            rejects { fixture.source.capture("meta") };assertEquals(0,fixture.downloads())
            fixture.metadataDrive="actual-drive";val captured=fixture.source.capture("meta")
            assertArrayEquals("{}".toByteArray(),fixture.source.readCaptured(captured));assertEquals(1,fixture.downloads())
        }
    }

    @Test fun actualReadChecksAccountParentRevisionAndImmutableHash() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.metadataSha = fixture.expectedHash
            assertArrayEquals("{}".toByteArray(),fixture.source.read(fixture.reference()))
            assertEquals(1,fixture.downloads()); assertTrue(fixture.requests.count { it.requestUrl?.encodedPath == "/drive/v3/about" } >= 4)
            assertTrue(fixture.requests.all { it.getHeader("Authorization") == "Bearer synthetic-access" })
        }
    }
    @Test fun binaryChunksAreListedAndPageTokensRemainExplicit() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.mime = "application/gzip"; fixture.pageToken = "next_opaque"
            val page = fixture.source.page("previous_opaque")
            assertEquals("application/gzip",page.files.single().mimeType); assertEquals("next_opaque",page.nextPage)
            val request = fixture.requests.single { it.requestUrl?.encodedPath == "/drive/v3/files" }
            val query = checkNotNull(request.requestUrl).queryParameter("q")
            assertEquals("'${fixture.pin.workspace.folderId}' in parents and trashed = false",query)
            assertEquals("previous_opaque",request.requestUrl?.queryParameter("pageToken"))
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun incompleteOrForeignPagesCannotClaimACompleteInventory() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.incomplete = true; rejects { fixture.source.page() }
            fixture.incomplete = false; fixture.parent = "foreign"; rejects { fixture.source.page() }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun originalMediaAndForeignReferenceAreNeverDownloaded() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.mime = "image/jpeg"; rejects { fixture.source.read(fixture.reference()) }
            fixture.mime = "application/json"
            rejects { fixture.source.read(fixture.reference().copy(accountId="foreign")) }
            rejects { fixture.source.read(fixture.reference().copy(workspaceId="foreign")) }
            rejects { fixture.source.read(fixture.reference().copy(fileId=fixture.pin.workspace.folderId)) }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun changedAccountWorkspaceAndMetadataRejectBeforeDownload() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.principal = "foreign"; rejects { fixture.source.read(fixture.reference()) }
            fixture.principal = fixture.pin.workspace.accountId; fixture.workspaceMime = "image/jpeg"
            rejects { fixture.source.read(fixture.reference()) }
            fixture.workspaceMime = DriveGateway.FOLDER_MIME; fixture.parent = "foreign"
            rejects { fixture.source.read(fixture.reference()) }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun changedRevisionOrPrincipalDuringReadPreservesFailure() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.afterMedia = { fixture.version = "6" }
            rejects { fixture.source.read(fixture.reference()) }
            fixture.afterMedia = { fixture.principal = "foreign" }
            rejects { fixture.source.read(fixture.reference()) }
            assertEquals(2,fixture.downloads())
        }
    }
    @Test fun capturedCandidateCannotBeSubstitutedBeforeItsRead() = runBlocking {
        Fixture(pin()).use { fixture ->
            val expected = fixture.source.page().files.single(); fixture.version = "6"
            rejects { fixture.source.readCaptured(expected) }; assertEquals(0,fixture.downloads())
        }
    }
    @Test fun lengthOverflowTruncationPartialAndDigestChangesAllFail() = runBlocking {
        Fixture(pin()).use { fixture ->
            for (response in listOf({ MockResponse().setChunkedBody("{}x",1) },{ MockResponse().setBody("x") },
                { MockResponse().setBody("{} ") },{ MockResponse().setBody("{}" ).setResponseCode(206) },{ MockResponse().setBody("[]") })) {
                fixture.media = response; rejects { fixture.source.read(fixture.reference()) }
            }
            assertEquals(5,fixture.downloads())
        }
    }
    @Test fun workspacePageTransportIsBoundedAndMalformedRowsAreRejected() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.page = { MockResponse().setChunkedBody(" ".repeat(1024 * 1024 + 1),1024) }
            rejects { fixture.source.page() }
            fixture.page = { MockResponse().setBody("""{"files":[${fixture.metadata().replace("\"version\":\"5\"","\"version\":\"05\"") }]}""") }
            rejects { fixture.source.page() }
            fixture.page = { MockResponse().setBody("""{"files":[${fixture.metadata()},${fixture.metadata()}]}""") }
            rejects { fixture.source.page() }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun blockedMetadataCannotStartADownload() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.downloadable = false
            rejects { fixture.source.read(fixture.reference()) }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun actualAccountRepliesAreBoundedBeforeAnyCatalogRead() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.accountReply = { MockResponse().setChunkedBody(" ".repeat(16385),1024) }
            rejects { fixture.source.capture("meta") }
            fixture.accountReply = { MockResponse().setBody("""{"user":{"permissionId":"foreign","emailAddress":"synthetic@example.invalid"}}""") }
            rejects { fixture.source.capture("meta") }
            assertEquals(0,fixture.downloads())
            assertTrue(fixture.requests.all { it.requestUrl?.encodedPath == "/drive/v3/about" })
        }
    }
    @Test fun actualCursorIsBoundedAndKeepsItsGoogleAccountBinding() = runBlocking {
        Fixture(pin()).use { fixture ->
            assertEquals("actual_cursor",fixture.source.cursor().token)
            assertEquals(0,fixture.downloads())
            assertEquals(fixture.pin.documentSha256,fixture.source.connectionSha256)
            assertEquals(1,fixture.requests.count { it.requestUrl?.encodedPath == "/drive/v3/changes/startPageToken" })
        }
    }
    @Test fun pageDataIsOwnedAndUnknownStructuresDoNotBecomeEmptySuccess() {
        val empty = DriveWorkspacePage.decode("""{"files":[]}""".toByteArray())
        assertTrue(empty.files.isEmpty())
        for (json in listOf("{}","[]","{","""{"files":[],"nextPageToken":""}""","""{"files":[],"incompleteSearch":"false"}""")) {
            try { DriveWorkspacePage.decode(json.toByteArray()); fail("Malformed workspace response became empty success") } catch (_: IOException) { }
        }
        assertThrows(UnsupportedOperationException::class.java) { (empty.files as MutableList<*>).clear() }
    }
    @Test fun actualHistoryCapturesAccountCorpusAndExplicitApiParameters() = runBlocking {
        Fixture(pin()).use { fixture ->
            fixture.sharedDrive = "actual-shared-drive"
            val cursor = fixture.source.cursor();assertEquals(fixture.pin.workspace.accountId,cursor.accountId);assertEquals("actual-shared-drive",cursor.sharedDriveId)
            fixture.history = { MockResponse().setBody("""{"changes":[{"changeType":"file","fileId":"meta","removed":false,"file":${fixture.metadata()}}],"newStartPageToken":"after"}""") }
            val page = fixture.source.changes(cursor);assertEquals("after",page.atEnd?.token);assertEquals("meta",page.changes.single().file?.id)
            val request = fixture.requests.single { it.requestUrl?.encodedPath == "/drive/v3/changes" };val url = checkNotNull(request.requestUrl)
            assertEquals("100",url.queryParameter("pageSize"));assertEquals("actual_cursor",url.queryParameter("pageToken"));assertEquals("actual-shared-drive",url.queryParameter("driveId"))
            assertEquals("true",url.queryParameter("includeRemoved"));assertEquals("true",url.queryParameter("includeCorpusRemovals"));assertEquals(0,fixture.downloads())
        }
    }
    @Test fun changedHistoryPrincipalOrCorpusRejectsAndDoesNotDownloadOriginals() = runBlocking {
        Fixture(pin()).use { fixture ->
            val cursor = fixture.source.cursor()
            rejects { fixture.source.changes(cursor.copy(accountId="foreign")) };rejects { fixture.source.changes(cursor.copy(sharedDriveId="foreign")) }
            assertTrue(fixture.requests.none { it.requestUrl?.encodedPath == "/drive/v3/changes" })
            fixture.afterHistory = { fixture.principal = "substituted" };rejects { fixture.source.changes(cursor) };assertEquals(0,fixture.downloads())
        }
        Fixture(pin()).use { fixture ->
            val cursor = fixture.source.cursor();fixture.afterHistory = { fixture.sharedDrive = "moved-drive" }
            rejects { fixture.source.changes(cursor) };assertEquals(0,fixture.downloads())
        }
    }
    @Test fun malformedAmbiguousAndNonadvancingHistoryCannotAdvance() = runBlocking {
        Fixture(pin()).use { fixture ->
            val cursor = fixture.source.cursor()
            for(body in listOf("{}","""{"changes":[],"nextPageToken":"n","newStartPageToken":"e"}""",
                """{"changes":[],"nextPageToken":"actual_cursor"}""","""{"changes":[{"changeType":"file","fileId":"meta","removed":false}],"newStartPageToken":"e"}""",
                """{"changes":[{"changeType":"file","fileId":"other","removed":false,"file":${fixture.metadata()}}],"newStartPageToken":"e"}""")) {
                fixture.history = { MockResponse().setBody(body) };rejects { fixture.source.changes(cursor) }
            }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun historyResponsesAndProviderErrorsRemainBounded() = runBlocking {
        Fixture(pin()).use { fixture ->
            val cursor = fixture.source.cursor()
            fixture.history = { MockResponse().setBody(" ".repeat(512 * 1024 + 1)) };rejects { fixture.source.changes(cursor) }
            fixture.history = { MockResponse().setResponseCode(401).setBody("unread-provider-body".repeat(40000)) };rejects { fixture.source.changes(cursor) }
            assertEquals(0,fixture.downloads())
        }
    }
    @Test fun removalKeepsActualIdentityAndWrongScopedCursorCannotBeConstructed() = runBlocking<Unit> {
        Fixture(pin()).use { fixture ->
            val cursor = fixture.source.cursor();fixture.history = { MockResponse().setBody("""{"changes":[{"changeType":"file","fileId":"removed-id","removed":true}],"newStartPageToken":"actual_cursor"}""") }
            val removed = fixture.source.changes(cursor).changes.single();assertEquals("removed-id",removed.id);assertTrue(removed.removed);assertNull(removed.file)
            assertThrows(IOException::class.java) { DriveWorkspaceCursor("root",null,"token") };assertThrows(IOException::class.java) { cursor.copy(token="bad\n") }
        }
    }

    private companion object {
        fun hash(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
    }
}
