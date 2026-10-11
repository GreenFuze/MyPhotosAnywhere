package com.myphotosanywhere.mpa.importing

import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.Buffer
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class TakeoutDownloadTest {
    private fun zip(): ByteArray = ByteArrayOutputStream().apply {
        ZipOutputStream(this).use { it.putNextEntry(ZipEntry("Takeout/Photos/Album/photo.jpg")); it.write(byteArrayOf(1,2,3,4)); it.closeEntry() }
    }.toByteArray()
    private fun downloader(space: Long=Long.MAX_VALUE) = TakeoutZipDownloader(validateUrl={},availableBytes={ space })
    private fun response(bytes: ByteArray) = MockResponse().setHeader("Content-Type","application/zip").setHeader("ETag","\"revision\"").setBody(Buffer().write(bytes))

    @Test fun streamZipWithoutCredentialsAndPersistReady() = runBlocking<Unit> {
        val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
        try {
            val bytes=zip(); server.enqueue(response(bytes)); var checkpoint=TakeoutDownload("job","encrypted")
            val result=downloader().download(server.url("/export?secret=private").toString(),file,checkpoint,{ checkpoint=it },{})
            assertArrayEquals(bytes,file.readBytes()); assertEquals("READY",result.state); assertEquals(result,checkpoint)
            val request=server.takeRequest(); assertNull(request.getHeader("Authorization")); assertNull(request.getHeader("Cookie"))
            assertEquals("identity",request.getHeader("Accept-Encoding"))
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun restartResumesExactRevisionAndTruncatesUncommittedBytes() = runBlocking<Unit> {
        val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
        try {
            val bytes=zip(); file.writeBytes(bytes.copyOfRange(0,20)+byteArrayOf(99,99))
            val saved=TakeoutDownload("job","encrypted",20,bytes.size.toLong(),"\"revision\"")
            server.enqueue(response(bytes.copyOfRange(20,bytes.size)).setResponseCode(206).setHeader("Content-Range","bytes 20-${bytes.size-1}/${bytes.size}"))
            val result=downloader().download(server.url("/zip").toString(),file,saved,{}, {})
            assertEquals("READY",result.state); assertArrayEquals(bytes,file.readBytes())
            val request=server.takeRequest(); assertEquals("bytes=20-",request.getHeader("Range")); assertEquals(saved.etag,request.getHeader("If-Range"))
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun ignoredRangeRestartsAndNeverAppendsAnotherRevision() = runBlocking<Unit> {
        val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
        try {
            val bytes=zip(); file.writeBytes(ByteArray(20) { 42 })
            server.enqueue(response(bytes).setHeader("ETag","\"new\""))
            val result=downloader().download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher",20,500,"\"old\""),{}, {})
            assertArrayEquals(bytes,file.readBytes()); assertEquals("\"new\"",result.etag)
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun weakOrMissingValidatorRestartsFromZero() = runBlocking<Unit> {
        for(etag in listOf(null,"W/\"weak\"")) {
            val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
            try {
                val bytes=zip(); file.writeBytes(ByteArray(20))
                server.enqueue(response(bytes))
                downloader().download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher",20,500,etag),{}, {})
                assertArrayEquals(bytes,file.readBytes()); assertNull(server.takeRequest().getHeader("Range"))
            } finally { server.shutdown(); file.delete() }
        }
    }
    @Test fun changedEtagOrWrongRangeNeverMutatesCheckpointBytes() = runBlocking<Unit> {
        for(wrongEtag in listOf(true,false)) {
            val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
            try {
                val bytes=zip(); val before=bytes.copyOfRange(0,20); file.writeBytes(before)
                val response=response(bytes.copyOfRange(20,bytes.size)).setResponseCode(206)
                    .setHeader("Content-Range","bytes ${if(wrongEtag) 20 else 19}-${bytes.size-1}/${bytes.size}")
                if(wrongEtag) response.setHeader("ETag","\"changed\"")
                server.enqueue(response)
                try { downloader().download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher",20,bytes.size.toLong(),"\"revision\""),{}, {}); fail() }
                catch(_: IllegalArgumentException) { }
                assertArrayEquals(before,file.readBytes())
            } finally { server.shutdown(); file.delete() }
        }
    }
    @Test fun crashAfterLastFsyncBecomesReadyWithoutAnInvalidEofRangeRequest() = runBlocking<Unit> {
        val file=File.createTempFile("takeout",".zip")
        try {
            val bytes=zip(); file.writeBytes(bytes)
            val saved=TakeoutDownload("job","cipher",bytes.size.toLong(),bytes.size.toLong(),"\"revision\"")
            val result=downloader().download("https://example.test/zip",file,saved,{}, {})
            assertEquals("READY",result.state); assertArrayEquals(bytes,file.readBytes())
        } finally { file.delete() }
    }
    @Test fun interruptionResumesFromDurableCheckpoint() = runBlocking<Unit> {
        val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
        try {
            val bytes=zip()+ByteArray(2*1024*1024) { (it%251).toByte() }
            var saved=TakeoutDownload("job","cipher")
            server.enqueue(response(bytes))
            try {
                downloader().download(server.url("/zip").toString(),file,saved,{ saved=it
                    if(it.bytes>=1024*1024) throw java.io.IOException("simulated process death")
                },{})
                fail()
            } catch(_: java.io.IOException) { }
            assertTrue(saved.bytes>=1024*1024); assertEquals(saved.bytes,file.length())
            val offset=saved.bytes.toInt()
            server.enqueue(response(bytes.copyOfRange(offset,bytes.size)).setResponseCode(206)
                .setHeader("Content-Range","bytes $offset-${bytes.size-1}/${bytes.size}"))
            downloader().download(server.url("/zip").toString(),file,saved,{}, {})
            assertArrayEquals(bytes,file.readBytes()); server.takeRequest()
            assertEquals("bytes=$offset-",server.takeRequest().getHeader("Range"))
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun rejectSigninPagesExpiredLinksAndNonZipBytes() = runBlocking<Unit> {
        for(response in listOf(MockResponse().setHeader("Content-Type","text/html").setBody("sign in"),
            MockResponse().setResponseCode(403),MockResponse().setBody("not-a-zip"))) {
            val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
            try {
                server.enqueue(response)
                try { downloader().download(server.url("/private?secret=hidden").toString(),file,TakeoutDownload("job","cipher"),{}, {}); fail() }
                catch(e: IllegalArgumentException) { assertFalse(e.message.orEmpty().contains("secret")) }
            } finally { server.shutdown(); file.delete() }
        }
    }
    @Test fun redirectsRevalidateEachTargetAndHaveABound() = runBlocking<Unit> {
        val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
        try {
            server.enqueue(MockResponse().setResponseCode(302).setHeader("Location","http://unsafe.test/zip"))
            val seen=mutableListOf<String>()
            val downloader=TakeoutZipDownloader(validateUrl={ seen.add(it); require(!it.contains("unsafe.test")) },availableBytes={ Long.MAX_VALUE })
            try { downloader.download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher"),{}, {}); fail() }
            catch(_: IllegalArgumentException) { }
            assertTrue(seen.last().contains("unsafe.test")); assertEquals(1,server.requestCount)
            repeat(6) { server.enqueue(MockResponse().setResponseCode(302).setHeader("Location","/again")) }
            try { downloader().download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher"),{}, {}); fail() }
            catch(_: IllegalArgumentException) { }
            assertEquals(7,server.requestCount)
        } finally { server.shutdown(); file.delete() }
    }
    @Test fun lowSpaceAndOversizeFailBeforeWritingArchiveBytes() = runBlocking<Unit> {
        for(oversize in listOf(true,false)) {
            val server=MockWebServer(); server.start(); val file=File.createTempFile("takeout",".zip")
            try {
                val response=response(zip())
                if(oversize) response.setHeader("Content-Length",TakeoutZipDownloader.MAX_ARCHIVE+1)
                server.enqueue(response)
                try { downloader(if(oversize) Long.MAX_VALUE else 1).download(server.url("/zip").toString(),file,TakeoutDownload("job","cipher"),{}, {}); fail() }
                catch(_: IllegalArgumentException) { }
                assertEquals(0,file.length())
            } finally { server.shutdown(); file.delete() }
        }
    }
    @Test fun explicitHttpsValidationRejectsCredentialsAndUnsafeSchemes() {
        for(url in listOf("http://example.test/x.zip","file:///x.zip","https://user:password@example.test/x.zip","https://example.test/x.zip#fragment","invalid")) {
            try { TakeoutZipDownloader.validateLink(url); fail(url) } catch(_: IllegalArgumentException) { } catch(_: IllegalStateException) { }
        }
        TakeoutZipDownloader.validateLink("https://example.test/export?signature=private")
    }
    @Test fun cleanupRequiresEveryMediaItemCommittedAndKeepsUnsupportedOrDeselectedBytes() {
        val photo=ImportItem("photo","job","uri","p.jpg","p.jpg","image/jpeg",4,state="COMPLETE")
        val json=photo.copy(id="json",mime="application/json",state="UNRESOLVED",metadata="{\"retained\":true}")
        assertTrue(TakeoutArchiveStore.canClean(listOf(photo,json)))
        assertFalse(TakeoutArchiveStore.canClean(emptyList()))
        for(state in listOf("PENDING","STAGING","UPLOADING","VERIFIED","FAILED","UNSUPPORTED")) {
            assertFalse(TakeoutArchiveStore.canClean(listOf(photo.copy(state=state,selected=false))))
        }
        assertFalse(TakeoutArchiveStore.canClean(listOf(photo,json.copy(metadata=null))))
    }
}
