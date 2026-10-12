package com.myphotosanywhere.mpa.importing

import android.app.Application
import com.myphotosanywhere.mpa.drive.*
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.*
import okio.Buffer
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class DriveTakeoutArchiveTest {
    @Test fun remoteZipStreamsMetadataAndSelectedMediaWithoutAWholeArchiveDownload() = runBlocking<Unit> {
        val media=ByteArray(900_000).apply { java.util.Random(1).nextBytes(this) }
        val raw=ByteArrayOutputStream()
        ZipOutputStream(raw).use { zip ->
            for((path,data) in listOf("Takeout/album/a.jpg" to media,
                "Takeout/album/a.jpg.supplemental-metadata.json" to "{\"title\":\"a.jpg\",\"description\":\"from Drive\",\"photoTakenTime\":{\"timestamp\":\"1\"}}".toByteArray())) {
                zip.putNextEntry(ZipEntry(path)); zip.write(data); zip.closeEntry()
            }
        }
        val bytes=raw.toByteArray(); var total=0L
        val server=MockWebServer()
        server.dispatcher=object: Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                assertEquals("Bearer test",request.getHeader("Authorization"))
                if(request.requestUrl?.queryParameter("alt")!="media") return MockResponse().setBody(
                    "{\"id\":\"zip\",\"name\":\"takeout.zip\",\"mimeType\":\"application/zip\",\"size\":\"${bytes.size}\",\"version\":\"1\",\"parents\":[\"root\"]}")
                val range=requireNotNull(request.getHeader("Range")).removePrefix("bytes=").split('-').map { it.toInt() }
                val slice=bytes.copyOfRange(range[0],range[1]+1); total+=slice.size
                assertTrue(slice.size<=DriveRangeChannel.BLOCK)
                request.getHeader("If-Match")?.let { assertEquals("\"revision\"",it) }
                return MockResponse().setResponseCode(206).setHeader("ETag","\"revision\"")
                    .setHeader("Content-Range","bytes ${range[0]}-${range[1]}/${bytes.size}").setBody(Buffer().write(slice))
            }
        }
        server.start()
        val target=File(RuntimeEnvironment.getApplication().cacheDir,"drive-zip-stage.jpg")
        try {
            val api=DriveGateway("test",OkHttpClient(),server.url("/drive/"),server.url("/upload"))
            val archive=DriveTakeoutArchive(api,"binding","root"); val uri=archive.capture("zip")
            // Robolectric has no mounted-volume UUIDs. Keep this streaming test independent
            // of platform capacity; StorageBudgetTest and native checks cover that contract.
            val storage=com.myphotosanywhere.mpa.data.StorageBudget(object: com.myphotosanywhere.mpa.data.StorageBudget.Capacity {
                override fun available(directory: File)=Long.MAX_VALUE
                override fun allocatable(directory: File)=Long.MAX_VALUE
                override fun allocate(directory: File,bytes: Long)=error("No reclamation expected")
            })
            val reader=TakeoutDocuments(RuntimeEnvironment.getApplication(),storage) { archive.open(it,4L*1024*1024) }
            val manifest=reader.manifest("job",uri,false)
            assertEquals(2,manifest.size); assertEquals("MATCHED",manifest.single { it.mime=="application/json" }.state)
            assertTrue(total<bytes.size) // Preview seeks to metadata near the end, not through all media.
            val item=manifest.single { it.mime=="image/jpeg" }
            assertTrue(requireNotNull(item.metadata).contains("from Drive"))
            val hash=reader.stage(item,target,{})
            assertArrayEquals(media,target.readBytes()); assertEquals(item.crc,hash.crc)
            assertFalse(uri.toString().contains("test")); assertEquals("mpa-drive-zip",uri.scheme)
        } finally { target.delete(); server.shutdown() }
    }

    @Test fun ignoredShiftedChangedAndTruncatedHttpRangesAreRejected() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            val api=DriveGateway("test",OkHttpClient(),server.url("/drive/"),server.url("/upload"))
            val good= { MockResponse().setResponseCode(206).setHeader("ETag","tag").setHeader("Content-Range","bytes 0-2/10").setBody("abc") }
            for(response in listOf(MockResponse().setBody("whole archive"),
                good().setHeader("Content-Range","bytes 1-3/10"),good().setHeader("ETag","new"),
                good().setBody("ab"),good().setBody("abcd"),good().removeHeader("ETag"))) {
                server.enqueue(response)
                try { api.readRange("zip",0,3,10,"tag"); fail("Unsafe range accepted") }
                catch(_: IllegalArgumentException) { } catch(_: IllegalStateException) { }
            }
            server.enqueue(MockResponse().setResponseCode(412))
            try { api.readRange("zip",0,3,10,"tag"); fail() } catch(e: DriveHttpException) { assertEquals(412,e.status) }
        } finally { server.shutdown() }
    }
    @Test fun changedOrMovedArchiveCannotReuseThePreviewRevision() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            val api=DriveGateway("test",OkHttpClient(),server.url("/drive/"),server.url("/upload"))
            val archive=DriveTakeoutArchive(api,"binding","root")
            val uri=android.net.Uri.parse("mpa-drive-zip://binding/zip?version=1&bytes=10&etag=tag")
            server.enqueue(MockResponse().setBody("{\"id\":\"zip\",\"name\":\"takeout.zip\",\"size\":\"10\",\"version\":\"2\",\"parents\":[\"root\"]}"))
            try { archive.open(uri,100); fail() } catch(e: IllegalArgumentException) { assertTrue(requireNotNull(e.message).contains("changed since preview")) }
            server.enqueue(MockResponse().setBody("{\"id\":\"zip\",\"name\":\"takeout.zip\",\"size\":\"10\",\"version\":\"1\",\"parents\":[]}"))
            try { archive.open(uri,100); fail() } catch(e: IllegalArgumentException) { assertTrue(requireNotNull(e.message).contains("outside")) }
            assertEquals(2,server.requestCount)
        } finally { server.shutdown() }
    }
}
