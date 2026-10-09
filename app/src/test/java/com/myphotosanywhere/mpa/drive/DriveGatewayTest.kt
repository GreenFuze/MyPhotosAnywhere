package com.myphotosanywhere.mpa.drive

import java.io.File
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class DriveGatewayTest {
    private lateinit var server: MockWebServer
    private lateinit var api: DriveGateway

    @Before fun setup() {
        server = MockWebServer().apply { start() }
        api = DriveGateway("test-token", OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).build(),
            server.url("/drive/v3/"), server.url("/upload/drive/v3/files"))
    }
    @After fun close() { server.shutdown() }

    @Test fun folderQueryEscapesLiteralsAndIncludesPagination() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"nextPageToken":"next","files":[{"id":"f","name":"Family","mimeType":"application/vnd.google-apps.folder","capabilities":{"canAddChildren":true}}]}"""))
        val page = api.folders("a'b\\c", "page-2")
        assertEquals("next", page.nextPage)
        assertTrue(page.files.single().canAddChildren)
        val request = server.takeRequest()
        assertEquals("Bearer test-token", request.getHeader("Authorization"))
        assertEquals("page-2", request.requestUrl?.queryParameter("pageToken"))
        assertTrue(requireNotNull(request.requestUrl?.queryParameter("q")).contains("'a\\'b\\\\c' in parents"))
    }

    @Test fun resumableUploadUsesReservedIdParentAndAcknowledgedOffset() = runBlocking {
        val session = server.url("/upload/drive/v3/files?upload_id=session").toString()
        server.enqueue(MockResponse().setResponseCode(200).setHeader("Location", session))
        val returned = api.beginUpload("reserved", "photo.jpg", "image/jpeg", 10, "folder", "asset", "source")
        assertEquals(session, returned)
        val create = server.takeRequest()
        val metadata = com.google.gson.JsonParser.parseString(create.body.readUtf8()).asJsonObject
        assertEquals("reserved", metadata.get("id").asString)
        assertEquals("folder", metadata.getAsJsonArray("parents")[0].asString)
        assertEquals("source", metadata.getAsJsonObject("appProperties").get("mpaParentId").asString)
        server.enqueue(MockResponse().setResponseCode(308).setHeader("Range", "bytes=0-4"))
        val progress = api.probe(session, 10)
        assertEquals(5L, progress.offset)
        assertFalse(progress.complete)
        assertEquals("bytes */10", server.takeRequest().getHeader("Content-Range"))
        server.enqueue(MockResponse().setResponseCode(201).setBody("{}"))
        assertTrue(api.chunk(session, 10, 5, ByteArray(5)).complete)
        assertEquals("bytes 5-9/10", server.takeRequest().getHeader("Content-Range"))
    }

    @Test fun rejectsUploadSessionThatWouldLeakAuthorizationToAnotherHost() = runBlocking {
        server.enqueue(MockResponse().setHeader("Location", "https://untrusted.example/upload/drive/v3/files"))
        try {
            api.beginUpload("id", "photo", "image/jpeg", 10, "folder", "asset", null)
            throw AssertionError("Expected invalid upload destination")
        } catch (_: IllegalArgumentException) { /* No request may follow this Location. */ }
        assertEquals(1, server.requestCount)
    }

    @Test fun rateLimitsRetryWhilePermissionFailuresNeedUserAction() = runBlocking {
        server.enqueue(MockResponse().setResponseCode(403).setBody("""{"error":{"errors":[{"reason":"userRateLimitExceeded"}]}}"""))
        try { api.user(); throw AssertionError("Expected HTTP error") }
        catch (error: DriveHttpException) { assertTrue(error.retryable) }
        server.enqueue(MockResponse().setResponseCode(403).setBody("""{"error":{"errors":[{"reason":"insufficientFilePermissions"}]}}"""))
        try { api.user(); throw AssertionError("Expected HTTP error") }
        catch (error: DriveHttpException) { assertFalse(error.retryable) }
    }

    @Test fun downloadsRespectTheImportLimitWithoutBufferingWholeImages() = runBlocking {
        server.enqueue(MockResponse().setBody("0123456789"))
        val file = File.createTempFile("mpa-download", ".part")
        try {
            try { api.download("id", file, 5); throw AssertionError("Expected size limit") }
            catch (_: IllegalStateException) { assertEquals(0L, file.length()) }
        } finally { file.delete() }
    }
}
