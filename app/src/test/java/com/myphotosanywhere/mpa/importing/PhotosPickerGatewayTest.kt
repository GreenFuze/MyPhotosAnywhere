package com.myphotosanywhere.mpa.importing

import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test

class PhotosPickerGatewayTest {
    @Test fun sessionPollingAndPersistentItemsUseOfficialContract() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            server.enqueue(MockResponse().setBody("""{"id":"session","pickerUri":"https://photos.google.com/picker/session","mediaItemsSet":false,"pollingConfig":{"pollInterval":"3.5s","timeoutIn":"120s"}}"""))
            server.enqueue(MockResponse().setBody("""{"mediaItems":[{"id":"persistent","createTime":"2025-01-01T00:00:00Z","type":"VIDEO","mediaFile":{"filename":"video.mp4","mimeType":"video/mp4","baseUrl":"https://lh3.googleusercontent.com/video","mediaFileMetadata":{"videoMetadata":{"processingStatus":"READY"}}}}],"nextPageToken":"page2"}"""))
            val api=PhotosPickerGateway("secret-test",api=server.url("/v1/"))
            val session=api.create(); assertEquals(3500L,session.interval); assertEquals(120000L,session.timeout)
            val result=api.items(session.id)
            assertEquals("persistent",result.first.single().id); assertTrue(result.first.single().videoReady)
            assertEquals("page2",result.second)
            val create=server.takeRequest(); assertEquals("POST",create.method); assertEquals("Bearer secret-test",create.getHeader("Authorization"))
            val list=server.takeRequest(); assertEquals("session",list.requestUrl?.queryParameter("sessionId"))
            assertFalse(requireNotNull(list.path).contains("secret-test"))
        } finally { server.shutdown() }
    }
}
