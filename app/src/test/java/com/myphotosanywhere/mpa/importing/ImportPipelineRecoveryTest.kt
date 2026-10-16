package com.myphotosanywhere.mpa.importing

import android.graphics.Bitmap
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.metadata.XmpCodec
import kotlinx.coroutines.runBlocking
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowBitmapFactory
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[27],application=MpaApplication::class)
class ImportPipelineRecoveryTest {
    private class Fixture: AutoCloseable {
        val app=RuntimeEnvironment.getApplication() as MpaApplication
        val database=app.database
        val id=java.util.UUID.randomUUID().toString()
        val source=DriveSource("$id-source","synthetic-account","import@test","APP_FILES","folder","Recovery fixture")
        val job=ImportJob(id,source.key,"TAKEOUT",state="RUNNING")
        val uploads=ConcurrentHashMap<String,ByteArray>();val metadata=ConcurrentHashMap<String,JsonObject>()
        val reservations=AtomicInteger();val starts=AtomicInteger()
        val server=MockWebServer()
        val files=mutableListOf<File>()
        val png: ByteArray
        val repository: ImportRepository
        init {
            val bitmap=Bitmap.createBitmap(64,96,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.BLUE);val output=ByteArrayOutputStream();check(bitmap.compress(Bitmap.CompressFormat.PNG,100,output));png=output.toByteArray() }
            finally { bitmap.recycle() }
            ShadowBitmapFactory.setAllowInvalidImageData(false)
            server.dispatcher=object: Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse {
                    val url=requireNotNull(request.requestUrl)
                    val path=url.encodedPath
                    return when {
                        path=="/drive/files/folder" -> json("""{"id":"folder","name":"Recovery","mimeType":"application/vnd.google-apps.folder","parents":[],"capabilities":{"canAddChildren":true}}""")
                        path=="/drive/files/generateIds" -> json("""{"ids":["remote-${reservations.incrementAndGet()}"]}""")
                        path.startsWith("/drive/files/") -> {
                            val remote=path.substringAfterLast('/');val bytes=uploads[remote]
                            if(bytes==null) MockResponse().setResponseCode(404) else {
                                val saved=requireNotNull(metadata[remote])
                                val row=JsonObject().apply {
                                    addProperty("id",remote);addProperty("name",saved.get("name").asString);addProperty("mimeType","image/png")
                                    addProperty("size",bytes.size.toString());addProperty("md5Checksum",digest(bytes,"MD5"));addProperty("version","1")
                                    add("parents",saved.get("parents"));add("appProperties",saved.get("appProperties"))
                                }
                                json(row.toString())
                            }
                        }
                        path=="/upload" && request.method=="POST" -> {
                            val saved=JsonParser.parseString(request.body.readUtf8()).asJsonObject;val remote=saved.get("id").asString
                            metadata[remote]=saved;starts.incrementAndGet()
                            MockResponse().setHeader("Location",server.url("/upload?upload_id=$remote"))
                        }
                        path=="/upload" && request.method=="PUT" -> {
                            val remote=requireNotNull(url.queryParameter("upload_id"));val bytes=request.body.readByteArray()
                            check(request.getHeader("Content-Range")=="bytes 0-${bytes.size-1}/${bytes.size}")
                            uploads[remote]=bytes;MockResponse().setResponseCode(200)
                        }
                        else -> MockResponse().setResponseCode(404)
                    }
                }
                private fun json(body: String)=MockResponse().setBody(body)
            }
            server.start(java.net.InetAddress.getByName("127.0.0.1"),0)
            val budget=StorageBudget(object: StorageBudget.Capacity {
                override fun available(directory: File)=1024L*1024*1024
                override fun allocatable(directory: File)=1024L*1024*1024
                override fun allocate(directory: File,bytes: Long)=error("Fixture must not reclaim cache")
            })
            repository=ImportRepository(app,object: ImportRuntime {
                override val storage=budget
                override suspend fun gateway(source: DriveSource,client: okhttp3.OkHttpClient?)=
                    DriveGateway("synthetic",api=server.url("/drive/"),upload=server.url("/upload"))
                override suspend fun committed(source: DriveSource) { check(source.key==this@Fixture.source.key) }
            })
        }
        suspend fun seed() { database.drive().saveSource(source);database.imports().saveJob(job) }
        fun zip(name: String,entry: String="photo.png",bytes: ByteArray=png): File {
            val file=File(app.filesDir,"takeout-downloads/$id-$name.zip")
            check(requireNotNull(file.parentFile).mkdirs() || requireNotNull(file.parentFile).isDirectory)
            ZipOutputStream(file.outputStream()).use { zip -> zip.putNextEntry(ZipEntry(entry));zip.write(bytes);zip.closeEntry() }
            files+=file;return file
        }
        fun item(name: String,path: String,file: File,metadata: String?=null,bytes: ByteArray=png)=
            ImportItem("$id-$name",job.id,file.toURI().toString(),path,path,"image/png",bytes.size.toLong(),
                crc=java.util.zip.CRC32().apply { update(bytes) }.value,metadata=metadata)
        suspend fun run(): Boolean=ImportExecution(database.imports(),{ repository.runOne(job.id) },{}).run(job.id)
        override fun close() {
            server.shutdown();ShadowBitmapFactory.setAllowInvalidImageData(true)
            runBlocking { database.imports().items(job.id).forEach { item ->
                File(app.filesDir,"migration-stage/${item.id}").delete();File(app.filesDir,"thumbnails/${item.id}.jpg").delete()
            } }
            database.close();files.forEach { it.delete() };app.deleteDatabase("mpa.db")
        }
        companion object {
            fun digest(bytes: ByteArray,algorithm: String)=java.security.MessageDigest.getInstance(algorithm).digest(bytes).joinToString("") { "%02x".format(it) }
        }
    }
    @Test fun missingArchiveRetryPreservesCompletedUpload()=runBlocking<Unit> {
        Fixture().use { fixture ->
            fixture.seed();val firstZip=fixture.zip("first","a.png");val savedZip=firstZip.readBytes();check(firstZip.delete())
            val secondZip=fixture.zip("second","b.png");val first=fixture.item("first","a.png",firstZip);val second=fixture.item("second","b.png",secondZip)
            val dao=fixture.database.imports();dao.insertItem(first);dao.insertItem(second)
            assertFalse(fixture.run())
            assertEquals("FAILED",dao.item(first.id)?.state);val done=requireNotNull(dao.item(second.id))
            assertEquals("Unexpected recovery receipt: $done; job=${dao.job(fixture.job.id)}","COMPLETE",done.state)
            assertEquals(1,fixture.starts.get());assertArrayEquals(fixture.png,fixture.uploads[done.remoteId])
            val photo=requireNotNull(fixture.database.photos().find(requireNotNull(done.photoId)))
            assertEquals(Fixture.digest(fixture.png,"SHA-256"),photo.sha256);assertEquals(fixture.source.key,photo.sourceBindingKey)
            firstZip.writeBytes(savedZip);ImportRetryPreparation(fixture.database).prepare(fixture.job.id)
            assertTrue(fixture.run());assertEquals(done,dao.item(second.id));assertEquals(photo,fixture.database.photos().find(photo.id))
            assertEquals("COMPLETE",dao.item(first.id)?.state);assertEquals(2,fixture.starts.get());assertEquals(2,fixture.reservations.get())
            assertEquals(fixture.job.binding,dao.job(fixture.job.id)?.binding)
            assertArrayEquals(savedZip,firstZip.readBytes());assertTrue(secondZip.isFile)
        }
    }
    @Test fun xmpCapacityDoesNotBlockVerifiedImport()=runBlocking<Unit> {
        Fixture().use { fixture ->
            fixture.seed();val db=fixture.database
            for(index in 0 until 1000) {
                val raw="""{"googleTakeout":{"title":"seed-$index.png","description":"${"annotation ".repeat(180)}","photoTakenTime":{"timestamp":"123"}}}"""
                val photo=Photo("seed-$index","seed-$index.png","","","image/png",1,123,"a".repeat(64),
                    sourceBindingKey=fixture.source.key,sourceRemoteId="seed-$index",cachedVersion="1",importedMetadata=raw,archived=true)
                db.photos().insert(photo);db.cloud().savePhoto(CloudPhoto("${fixture.source.key}:seed-$index",fixture.source.key,"seed-$index","folder",photo.displayName,
                    "image/png",1,"md5","1",null,null,null,null,photoId=photo.id,sha256=photo.sha256))
            }
            val before=fixture.app.metadata.snapshot(fixture.source)
            val capacity=assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(before) }
            assertTrue(capacity.message.orEmpty().contains("2 MiB"))
            val raw="""{"title":"photo.png","photoTakenTime":{"timestamp":"124"},"people":[{"name":"Synthetic person"}]}"""
            val zip=fixture.zip("metadata");val item=fixture.item("metadata","photo.png",zip,raw);db.imports().insertItem(item)
            val success=fixture.run()
            assertTrue("Unexpected recovery receipt: ${db.imports().item(item.id)}; job=${db.imports().job(fixture.job.id)}",success)
            val done=requireNotNull(db.imports().item(item.id));assertEquals("COMPLETE",done.state)
            assertArrayEquals(fixture.png,fixture.uploads[done.remoteId]);assertEquals(raw,done.metadata)
            val photo=requireNotNull(db.photos().find(requireNotNull(done.photoId)))
            assertEquals(Fixture.digest(fixture.png,"SHA-256"),photo.sha256);assertTrue(photo.importedMetadata.contains("Synthetic person"))
            val outbox=requireNotNull(db.metadata().state(fixture.source.key));assertTrue(outbox.generation>outbox.uploadedGeneration)
            val after=fixture.app.metadata.snapshot(fixture.source)
            val expected=before.assets.associateBy { it.id }
            assertEquals(expected,after.assets.filter { it.id in expected }.associateBy { it.id })
            assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(after) }
            assertTrue(zip.isFile);assertEquals("COMPLETE",db.imports().job(fixture.job.id)?.state)
        }
    }
}
