package com.myphotosanywhere.mpa.importing

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.metadata.*
import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test

class TakeoutCleanupTest {
    private val raw="""{"title":"p.jpg","photoTakenTime":{"timestamp":"123"},"description":"Keep unknown too","unknown":{"a":1}}"""
    private val sha="a".repeat(64)
    private val media=DriveFile("photo","p.jpg","image/jpeg",4,"hash","7",listOf("nested"))
    private val json=DriveFile("json","p.jpg.json","application/json",100,"json-hash","3",listOf("nested"))
    private val catalogFile="""{"id":"catalog","name":".mpa-catalog.xmp","mimeType":"application/rdf+xml","parents":["folder"]}"""
    private val remoteMedia="""{"id":"photo","name":"p.jpg","mimeType":"image/jpeg","size":"4","md5Checksum":"hash","version":"7","parents":["nested"]}"""
    private val remoteJson="""{"id":"json","name":"p.jpg.json","mimeType":"application/json","size":"100","md5Checksum":"json-hash","version":"3","parents":["nested"]}"""
    private fun xml(provider: String): String {
        val asset=PortableAsset("id","photo",sha,4,false,false,"",null,null,null,null,null,null,"",null,emptyList(),emptyList(),provider)
        return XmpCodec.encode(XmpCatalog("account","folder",listOf(asset),emptyList(),emptyList()))
    }
    @Test fun verifiedFullMetadataAllowsOnlyConditionalTrash() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            val photo=TakeoutCodec().apply(Photo("id","p.jpg","","","image/jpeg",4,1,sha),TakeoutCodec().parse(raw))
            server.enqueue(MockResponse().setBody(catalogFile))
            server.enqueue(MockResponse().setBody(xml(photo.importedMetadata)).setHeader("ETag","catalog-etag"))
            server.enqueue(MockResponse().setBody(remoteMedia)); server.enqueue(MockResponse().setBody(remoteJson))
            server.enqueue(MockResponse().setBody("{}"))
            TakeoutCleanup(DriveGateway("test",api=server.url("/drive/"))).verifyAndTrash("account","folder",json,"json-etag",raw,media,sha,"catalog",{})
            repeat(4) { assertEquals("GET",server.takeRequest().method) }
            val cleanup=server.takeRequest(); assertEquals("PATCH",cleanup.method)
            assertEquals("json-etag",cleanup.getHeader("If-Match")); assertEquals("{\"trashed\":true}",cleanup.body.readUtf8())
        } finally { server.shutdown() }
    }
    @Test fun missingSupplementalFieldsPreventJsonRemoval() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            server.enqueue(MockResponse().setBody(catalogFile))
            server.enqueue(MockResponse().setBody(xml("{\"googleTakeout\":{\"title\":\"p.jpg\",\"photoTakenTime\":{\"timestamp\":\"123\"}}}")).setHeader("ETag","etag"))
            try { TakeoutCleanup(DriveGateway("test",api=server.url("/drive/"))).verifyAndTrash("account","folder",json,"etag",raw,media,sha,"catalog",{}); fail() }
            catch(_: IllegalArgumentException) { }
            assertEquals(2,server.requestCount)
        } finally { server.shutdown() }
    }
    @Test fun modifiedJsonAndConcurrentWriterPreventCleanup() = runBlocking<Unit> {
        val server=MockWebServer(); server.start()
        try {
            server.enqueue(MockResponse().setBody(remoteJson.replace("\"3\"","\"4\"")))
            try { DriveGateway("test",api=server.url("/drive/")).trashTakeoutJson(json,"old-etag"); fail() } catch(_: IllegalArgumentException) { }
            assertEquals(1,server.requestCount); assertEquals("GET",server.takeRequest().method)
            server.enqueue(MockResponse().setBody(remoteJson)); server.enqueue(MockResponse().setResponseCode(412))
            try { DriveGateway("test",api=server.url("/drive/")).trashTakeoutJson(json,"old-etag"); fail() }
            catch(e: DriveHttpException) { assertEquals(412,e.status) }
            assertEquals("GET",server.takeRequest().method); assertEquals("old-etag",server.takeRequest().getHeader("If-Match"))
        } finally { server.shutdown() }
    }
}
