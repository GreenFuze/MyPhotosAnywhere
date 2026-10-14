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
    @Test fun changesPagePreservesRemovalAndMetadataWithItsCheckpoint() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"newStartPageToken":"checkpoint","changes":[
            {"fileId":"gone","removed":true},{"fileId":"photo","file":{"id":"photo","name":"family.jpg","mimeType":"image/jpeg",
            "parents":["folder"],"version":"2","md5Checksum":"md5","imageMediaMetadata":{"time":"2020-01-02T03:04:05Z","location":{"latitude":32.0,"longitude":34.0}}}}]}"""))
        val page=api.changes("old")
        assertEquals("checkpoint",page.newStart);assertTrue(page.changes.first().removed)
        assertEquals(32.0,page.changes.last().file?.latitude)
        assertEquals(1577934245000L,page.changes.last().file?.capturedAt)
        assertEquals("old",server.takeRequest().requestUrl?.queryParameter("pageToken"))
    }
    @Test fun recursiveListingIncludesVideosAndFoldersWithoutOtherFileTypes() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"files":[]}"""))
        api.children("folder")
        val query=requireNotNull(server.takeRequest().requestUrl?.queryParameter("q"))
        assertTrue(query.contains("mimeType contains 'video/'"));assertTrue(query.contains(DriveGateway.FOLDER_MIME))
        assertTrue(query.contains("'folder' in parents"))
    }
    @Test fun thumbnailHostIsValidatedBeforeAuthorizationCanBeSent() = runBlocking {
        val file=File.createTempFile("thumbnail",".part")
        try {
            try { api.thumbnail("https://untrusted.example/image",file);throw AssertionError("Expected host validation") }
            catch(_: IllegalArgumentException) { assertEquals(0,server.requestCount) }
        } finally { file.delete() }
    }

    @Test fun xmpUpdatesAreConditionalAndConflictsDoNotRetryAnOverwrite() = runBlocking {
        server.enqueue(MockResponse().setHeader("ETag", "\"revision-1\"").setBody("<xmp/>"))
        assertEquals("<xmp/>" to "\"revision-1\"",api.readMetadata("catalog"))
        assertEquals("media",server.takeRequest().requestUrl?.queryParameter("alt"))
        server.enqueue(MockResponse().setResponseCode(412).setBody("{}"))
        try { api.writeMetadata("catalog","folder","<new/>","\"revision-1\""); throw AssertionError("Expected conflict") }
        catch(e: DriveHttpException) { assertEquals(412,e.status); assertFalse(e.retryable) }
        val write=server.takeRequest()
        assertEquals("PATCH",write.method); assertEquals("\"revision-1\"",write.getHeader("If-Match"))
        assertEquals("/upload/drive/v3/files/catalog",write.requestUrl?.encodedPath)
        assertEquals(2,server.requestCount)
    }
    @Test fun xmpDiscoveryRejectsDuplicateCatalogsAndMissingRevision() = runBlocking {
        server.enqueue(MockResponse().setBody("""{"files":[{"id":"one"},{"id":"two"}]}"""))
        try { api.metadataFiles("folder"); throw AssertionError("Expected duplicates") }
        catch(_: IllegalArgumentException) { }
        assertTrue(requireNotNull(server.takeRequest().requestUrl?.queryParameter("q")).contains("'.mpa-catalog.xmp'"))
        server.enqueue(MockResponse().setBody("<xmp/>"))
        try { api.readMetadata("one"); throw AssertionError("Expected revision guard") }
        catch(_: IllegalStateException) { }
    }

    private fun renewing(refresh: suspend (String) -> String) = DriveGateway("old-token",
        OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).build(),
        server.url("/drive/v3/"),server.url("/upload/drive/v3/files"),refresh)

    @Test fun expiredAccessRenewsSilentlyAndTheSameGatewayUsesItsReplacement() = runBlocking {
        var calls=0
        val renewing=renewing { rejected -> assertEquals("old-token",rejected);calls++;"new-token" }
        server.enqueue(MockResponse().setResponseCode(401).setBody("rejected"))
        server.enqueue(MockResponse().setBody("""{"user":{"permissionId":"account","emailAddress":"user@example.test"}}"""))
        server.enqueue(MockResponse().setBody("""{"id":"photo","name":"photo.jpg"}"""))
        assertEquals("account",renewing.user().id);assertEquals("photo",renewing.file("photo").id)
        assertEquals("Bearer old-token",server.takeRequest().getHeader("Authorization"))
        assertEquals("Bearer new-token",server.takeRequest().getHeader("Authorization"))
        assertEquals("Bearer new-token",server.takeRequest().getHeader("Authorization"));assertEquals(1,calls)
    }
    @Test fun repeatedUnauthorizedResponseStopsAfterOneRefresh() = runBlocking {
        var calls=0;val renewing=renewing { calls++;"new-token" }
        repeat(2) { server.enqueue(MockResponse().setResponseCode(401)) }
        try { renewing.user();fail("Repeated unauthorized must surface") }
        catch(error: DriveHttpException) { assertEquals(401,error.status);assertFalse(error.retryable) }
        assertEquals(2,server.requestCount);assertEquals(1,calls)
    }
    @Test fun permissionAndServerFailuresNeverRefreshOrReplay() = runBlocking {
        val renewing=renewing { error("Must not refresh") }
        for(status in listOf(403,500)) {
            server.enqueue(MockResponse().setResponseCode(status))
            try { renewing.user();fail("HTTP failure must surface") }
            catch(error: DriveHttpException) { assertEquals(status,error.status) }
        }
        assertEquals(2,server.requestCount)
    }
    @Test fun tokenRenewalDuringUploadReplaysOnlyTheRejectedChunkWithIdenticalBytesAndRange() = runBlocking {
        val renewing=renewing { "new-token" };val bytes=byteArrayOf(0,1,2,3,4)
        server.enqueue(MockResponse().setResponseCode(401));server.enqueue(MockResponse().setResponseCode(308).setHeader("Range","bytes=0-9"))
        val session=server.url("/upload/drive/v3/files?upload_id=reserved-session").toString()
        assertEquals(10L,renewing.chunk(session,20,5,bytes).offset)
        val first=server.takeRequest();val retry=server.takeRequest()
        assertEquals(first.path,retry.path);assertEquals("PUT",retry.method)
        assertEquals("bytes 5-9/20",retry.getHeader("Content-Range"))
        assertArrayEquals(bytes,first.body.readByteArray());assertArrayEquals(bytes,retry.body.readByteArray())
        assertEquals("Bearer new-token",retry.getHeader("Authorization"));assertEquals(2,server.requestCount)
    }
    @Test fun rejectedDownloadBodyNeverReplacesTheOriginalAndVerifiedRetryIsReadOnce() = runBlocking {
        val renewing=renewing { "new-token" };val file=File.createTempFile("mpa-auth",".part")
        try {
            file.writeText("preserved")
            server.enqueue(MockResponse().setResponseCode(401).setBody("not-photo"));server.enqueue(MockResponse().setBody("photo"))
            renewing.download("photo",file,5)
            assertEquals("photo",file.readText());assertEquals(2,server.requestCount)
        } finally { file.delete() }
    }
    @Test fun cancellationWhileRenewingClosesRejectedResponseAndNeverReplaysOrWrites() = runBlocking {
        val expected=kotlinx.coroutines.CancellationException("cancelled")
        val renewing=renewing { throw expected };val file=File.createTempFile("mpa-cancel",".part")
        try {
            file.writeText("preserved");server.enqueue(MockResponse().setResponseCode(401).setBody("not-photo"))
            try { renewing.download("photo",file,50);fail("Cancellation must propagate") }
            catch(error: kotlinx.coroutines.CancellationException) { assertSame(expected,error) }
            assertEquals("preserved",file.readText());assertEquals(1,server.requestCount)
        } finally { file.delete() }
    }
    @Test fun realConsentRequirementDuringRenewalIsPreservedWithoutAnotherRequest() = runBlocking {
        val expected=DriveAuthorizationNeeded();val renewing=renewing { throw expected }
        server.enqueue(MockResponse().setResponseCode(401))
        try { renewing.user();fail("Real permission loss must surface") }
        catch(error: DriveAuthorizationNeeded) { assertSame(expected,error) }
        assertEquals(1,server.requestCount)
    }

}
