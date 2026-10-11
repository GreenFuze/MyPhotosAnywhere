package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.*
import okhttp3.mockwebserver.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportDuplicateFinderTest {
    private val sha="a".repeat(64)
    private val item=ImportItem("item","job","uri","p.jpg","p.jpg","image/jpeg",4,sha256=sha,md5="md5")
    private fun row(id: String="remote",binding: String="selected",hash: String=sha)=CloudPhoto("$binding:$id",binding,id,"nested","p.jpg","image/jpeg",4,"md5","1",null,null,null,null,sha256=hash)
    private fun remote(parent: String="nested",hash: String=sha,md5: String="md5",trash: Boolean=false)="""{"id":"remote","name":"p.jpg","mimeType":"image/jpeg","size":"4","md5Checksum":"$md5","sha256Checksum":"$hash","version":"2","parents":["$parent"],"trashed":$trash}"""
    private suspend fun fixture(block: suspend (PhotoDatabase,MockWebServer,ImportDuplicateFinder)->Unit) {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).allowMainThreadQueries().build()
        val server=MockWebServer(); server.start(java.net.InetAddress.getByName("127.0.0.1"),0)
        try { block(db,server,ImportDuplicateFinder(db.cloud(),DriveGateway("test",api=server.url("/drive/")))) }
        finally { server.shutdown(); db.close() }
    }
    @Test fun indexedExternalNestedFileIsVerifiedWithoutDownloadingItsOriginal() = runBlocking<Unit> {
        fixture { db,server,finder ->
            db.cloud().savePhoto(row()) // No Photo row or downloaded original.
            server.enqueue(MockResponse().setBody(remote()))
            server.enqueue(MockResponse().setBody("""{"id":"nested","name":"Album","mimeType":"application/vnd.google-apps.folder","parents":["root"]}"""))
            val match=requireNotNull(finder.find("selected","root",item,{ }))
            assertEquals("remote",match.remote.id); assertEquals("2",match.remote.version); assertNull(match.cached.photoId)
            repeat(2) {
                val request=server.takeRequest()
                assertEquals("GET",request.method)
                assertTrue(requireNotNull(request.path).contains("sha256Checksum"))
                assertFalse(requireNotNull(request.path).contains("alt=media"))
            }
        }
    }
    @Test fun equalHashInAnotherSourceOrRemovedFileIsNeverQueried() = runBlocking<Unit> {
        fixture { db,server,finder ->
            db.cloud().savePhoto(row(binding="another-account")); db.cloud().savePhoto(row().copy(removed=true))
            assertNull(finder.find("selected","root",item,{})); assertEquals(0,server.requestCount)
        }
    }
    @Test fun staleHashesMissingHashesAndTrashedOrMovedFilesCannotBeReused() = runBlocking<Unit> {
        fixture { db,server,finder ->
            db.cloud().savePhoto(row())
            for(json in listOf(remote(hash="b".repeat(64)),remote().replace("\"sha256Checksum\":\"$sha\",",""),remote(md5="changed"),remote(trash=true))) {
                server.enqueue(MockResponse().setBody(json)); assertNull(finder.find("selected","root",item,{}))
            }
            server.enqueue(MockResponse().setBody(remote(parent="outside")))
            server.enqueue(MockResponse().setBody("""{"id":"outside","name":"Other","mimeType":"application/vnd.google-apps.folder","parents":[]}"""))
            assertNull(finder.find("selected","root",item,{}))
        }
    }
    @Test fun missingCandidateIsSkippedButDeniedAccessFailsFast() = runBlocking<Unit> {
        fixture { db,server,finder ->
            db.cloud().savePhoto(row())
            server.enqueue(MockResponse().setResponseCode(404)); assertNull(finder.find("selected","root",item,{}))
            server.enqueue(MockResponse().setResponseCode(403))
            try { finder.find("selected","root",item,{}); fail() } catch(e: DriveHttpException) { assertEquals(403,e.status) }
        }
    }
    @Test fun boundedKeyPaginationFindsALiveCandidateBeyondFirstPage() = runBlocking<Unit> {
        fixture { db,server,finder ->
            repeat(33) { index ->
                db.cloud().savePhoto(row(id="remote-%02d".format(index)))
                server.enqueue(if(index<32) MockResponse().setResponseCode(404) else MockResponse().setBody(remote(parent="root").replace("\"remote\"","\"remote-32\"")))
            }
            assertEquals("remote-32",finder.find("selected","root",item,{})?.remote?.id)
            assertEquals(33,server.requestCount)
        }
    }
}
