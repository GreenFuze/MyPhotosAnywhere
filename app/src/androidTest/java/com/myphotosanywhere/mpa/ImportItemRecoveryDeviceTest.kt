package com.myphotosanywhere.mpa

import android.graphics.Bitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.drive.*
import com.myphotosanywhere.mpa.importing.*
import com.myphotosanywhere.mpa.metadata.XmpCodec
import kotlinx.coroutines.runBlocking
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/** Real Android decoding/Room/import execution; synthetic HTTP responses never open a network. */
@RunWith(AndroidJUnit4::class)
class ImportItemRecoveryDeviceTest {
    @get:Rule val compose=createAndroidComposeRule<MainActivity>()
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val app get()=instrumentation.targetContext.applicationContext as MpaApplication
    private fun waitTag(tag: String)=compose.waitUntil(30000) { compose.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty() }
    private fun waitCount(id: String,text: String)=compose.waitUntil(15000) {
        compose.onAllNodes(hasTestTag("import-count:$id") and hasText(text)).fetchSemanticsNodes().isNotEmpty()
    }
    private class Fixture(val app: MpaApplication): AutoCloseable {
        val database=app.database
        val id=java.util.UUID.randomUUID().toString()
        val source=DriveSource("$id-source","synthetic-account","import@test","APP_FILES","folder","Import recovery fixture")
        val job=ImportJob(id,source.key,"TAKEOUT",state="RUNNING")
        val uploads=ConcurrentHashMap<String,ByteArray>();val metadata=ConcurrentHashMap<String,JsonObject>()
        val reservations=AtomicInteger();val starts=AtomicInteger()
        val files=mutableListOf<File>();val sources=mutableListOf(source.key)
        val png: ByteArray
        val repository: ImportRepository
        init {
            val bitmap=Bitmap.createBitmap(64,96,Bitmap.Config.ARGB_8888)
            try { bitmap.eraseColor(android.graphics.Color.BLUE);val out=ByteArrayOutputStream();check(bitmap.compress(Bitmap.CompressFormat.PNG,100,out));png=out.toByteArray() }
            finally { bitmap.recycle() }
            val transport=OkHttpClient.Builder().followRedirects(false).followSslRedirects(false).addInterceptor { chain ->
                val request=chain.request();check(request.url.host=="synthetic.invalid")
                val path=request.url.encodedPath;var status=200;var location: String?=null
                val body=when {
                    path=="/drive/files/folder" -> """{"id":"folder","name":"Recovery","mimeType":"application/vnd.google-apps.folder","parents":[],"capabilities":{"canAddChildren":true}}"""
                    path=="/drive/files/generateIds" -> """{"ids":["remote-${reservations.incrementAndGet()}"]}"""
                    path.startsWith("/drive/files/") -> {
                        val remote=path.substringAfterLast('/');val bytes=uploads[remote]
                        if(bytes==null) { status=404;"{}" } else {
                            val saved=requireNotNull(metadata[remote])
                            JsonObject().apply {
                                addProperty("id",remote);addProperty("name",saved.get("name").asString);addProperty("mimeType","image/png")
                                addProperty("size",bytes.size.toString());addProperty("md5Checksum",digest(bytes,"MD5"));addProperty("version","1")
                                add("parents",saved.get("parents"));add("appProperties",saved.get("appProperties"))
                            }.toString()
                        }
                    }
                    path=="/upload" && request.method=="POST" -> {
                        val buffer=okio.Buffer();requireNotNull(request.body).writeTo(buffer)
                        val saved=JsonParser.parseString(buffer.readUtf8()).asJsonObject;val remote=saved.get("id").asString
                        metadata[remote]=saved;starts.incrementAndGet();location="https://synthetic.invalid/upload?upload_id=$remote";"{}"
                    }
                    path=="/upload" && request.method=="PUT" -> {
                        val remote=requireNotNull(request.url.queryParameter("upload_id"));val buffer=okio.Buffer();requireNotNull(request.body).writeTo(buffer)
                        val bytes=buffer.readByteArray();check(request.header("Content-Range")=="bytes 0-${bytes.size-1}/${bytes.size}")
                        uploads[remote]=bytes;"{}"
                    }
                    else -> error("Unexpected synthetic Drive request: $path")
                }
                Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(status).message("Synthetic")
                    .body(body.toResponseBody("application/json".toMediaType())).apply { if(location!=null) header("Location",requireNotNull(location)) }.build()
            }.build()
            repository=ImportRepository(app,object: ImportRuntime {
                override val storage=app.storage
                override suspend fun gateway(source: DriveSource,client: OkHttpClient?): DriveGateway {
                    check(source.key==this@Fixture.source.key)
                    check(client==null)
                    return DriveGateway("synthetic",transport,api="https://synthetic.invalid/drive/".toHttpUrl(),upload="https://synthetic.invalid/upload".toHttpUrl())
                }
                override suspend fun committed(source: DriveSource) { check(source.key==this@Fixture.source.key) }
            })
        }
        suspend fun seed() { database.drive().saveSource(source);database.imports().saveJob(job) }
        fun zip(name: String,entry: String="photo.png",bytes: ByteArray=png): File {
            val file=File(app.filesDir,"takeout-downloads/$id-$name.zip");check(requireNotNull(file.parentFile).mkdirs() || requireNotNull(file.parentFile).isDirectory)
            ZipOutputStream(file.outputStream()).use { zip -> zip.putNextEntry(ZipEntry(entry));zip.write(bytes);zip.closeEntry() }
            files+=file;return file
        }
        fun item(name: String,path: String,file: File,raw: String?=null,bytes: ByteArray=png)=
            ImportItem("$id-$name",job.id,file.toURI().toString(),path,path,"image/png",bytes.size.toLong(),
                crc=java.util.zip.CRC32().apply { update(bytes) }.value,metadata=raw)
        suspend fun run(): Boolean=ImportExecution(database.imports(),{ repository.runOne(job.id) },{}).run(job.id)
        override fun close() {
            runBlocking { database.withTransaction {
                database.imports().job(job.id)?.let { database.imports().saveJob(it.copy(state="CANCELLED")) }
                database.imports().items(job.id).forEach { item ->
                    File(app.filesDir,"migration-stage/${item.id}").delete();File(app.filesDir,"thumbnails/${item.id}.jpg").delete()
                }
                val sql=database.openHelper.writableDatabase
                for(key in sources) {
                    sql.execSQL("DELETE FROM cloud_photos WHERE bindingKey=?",arrayOf(key))
                    sql.execSQL("DELETE FROM photos WHERE sourceBindingKey=?",arrayOf(key))
                    database.drive().disconnectSource(key)
                    sql.execSQL("DELETE FROM metadata_states WHERE bindingKey=?",arrayOf(key))
                }
                sql.execSQL("DELETE FROM import_items WHERE job=?",arrayOf(job.id));sql.execSQL("DELETE FROM import_jobs WHERE id=?",arrayOf(job.id))
            } }
            files.forEach { it.delete() }
        }
        companion object {
            fun digest(bytes: ByteArray,algorithm: String)=java.security.MessageDigest.getInstance(algorithm).digest(bytes).joinToString("") { "%02x".format(it) }
        }
    }
    @Test fun failedImageContinuesThenHistoryRecreationAndRetryPreserveTheVerifiedSecondImage()=runBlocking<Unit> {
        Fixture(app).use { fixture ->
            fixture.seed();val firstZip=fixture.zip("first","a.png");val saved=firstZip.readBytes();check(firstZip.delete())
            val corruptZip=fixture.zip("corrupt","b.png",ByteArray(fixture.png.size));val corruptBytes=corruptZip.readBytes()
            val secondZip=fixture.zip("second","c.png");val first=fixture.item("first","a.png",firstZip)
            val corrupt=fixture.item("corrupt","b.png",corruptZip,bytes=ByteArray(fixture.png.size));val second=fixture.item("second","c.png",secondZip)
            val dao=app.database.imports();dao.insertItem(first);dao.insertItem(corrupt);dao.insertItem(second)
            assertTrue(fixture.repository.runOne(fixture.job.id));assertEquals("FAILED",dao.item(first.id)?.state);assertEquals("RUNNING",dao.job(fixture.job.id)?.state)
            assertTrue(fixture.repository.runOne(fixture.job.id));assertEquals("FAILED",dao.item(corrupt.id)?.state);assertNull(dao.item(corrupt.id)?.remoteId)
            compose.onNodeWithTag("shell:activity").performClick();waitTag("import-failed-count:${fixture.job.id}")
            compose.onNodeWithTag("import-failed-count:${fixture.job.id}").performScrollTo().assertTextContains("2 files need retry",substring=true)
            assertFalse(fixture.run());val completed=requireNotNull(dao.item(second.id));assertEquals("COMPLETE",completed.state)
            val photo=requireNotNull(app.database.photos().find(requireNotNull(completed.photoId)))
            assertArrayEquals(fixture.png,fixture.uploads[completed.remoteId]);assertEquals(Fixture.digest(fixture.png,"SHA-256"),photo.sha256)
            waitCount(fixture.job.id,"1/3 verified in Drive");compose.onNodeWithTag("import-count:${fixture.job.id}").performScrollTo().assertTextEquals("1/3 verified in Drive")
            screenshot("import-item-recovery")
            compose.activityRule.scenario.recreate();waitCount(fixture.job.id,"1/3 verified in Drive")
            compose.onNodeWithTag("import-count:${fixture.job.id}").performScrollTo().assertTextEquals("1/3 verified in Drive")
            assertEquals(completed,dao.item(second.id));assertEquals(photo,app.database.photos().find(photo.id))
            val other=fixture.source.copy(key="${fixture.id}-new-default",folderId="other",backupEnabled=true);app.database.drive().saveSource(other);fixture.sources+=other.key
            firstZip.writeBytes(saved);ImportRetryPreparation(app.database).prepare(fixture.job.id);assertFalse(fixture.run())
            assertEquals("COMPLETE",dao.item(first.id)?.state);val stillFailed=requireNotNull(dao.item(corrupt.id));assertEquals("FAILED",stillFailed.state)
            assertEquals(completed,dao.item(second.id));assertEquals(photo,app.database.photos().find(photo.id));assertEquals(2,fixture.starts.get());assertEquals(2,fixture.reservations.get())
            assertEquals(fixture.job.binding,dao.job(fixture.job.id)?.binding);assertArrayEquals(saved,firstZip.readBytes());assertTrue(secondZip.isFile)
            assertArrayEquals(corruptBytes,corruptZip.readBytes());assertEquals(1,dao.selectedFailureCount(fixture.job.id))
            // Excluding the still-corrupt input is an explicit selection choice, never an automatic deletion.
            dao.updateItem(stillFailed.copy(selected=false));ImportRetryPreparation(app.database).prepare(fixture.job.id);assertTrue(fixture.run())
            assertEquals(stillFailed.copy(selected=false),dao.item(corrupt.id));assertEquals(completed,dao.item(second.id));assertEquals(2,fixture.starts.get())
            waitCount(fixture.job.id,"2/2 verified in Drive")
            compose.onNodeWithTag("import-count:${fixture.job.id}").performScrollTo().assertTextEquals("2/2 verified in Drive");screenshot("import-retry-complete")
        }
    }
    @Test fun realAndroidImportKeepsMetadataWhenTheOptionalXmpCatalogueExceedsItsSafetyBudget()=runBlocking<Unit> {
        Fixture(app).use { fixture ->
            fixture.seed();val db=app.database
            db.withTransaction {
                for(index in 0 until 1000) {
                    val name="${fixture.id}-seed-$index"
                    val raw="""{"googleTakeout":{"title":"seed-$index.png","description":"${"annotation ".repeat(180)}","photoTakenTime":{"timestamp":"123"}}}"""
                    val photo=Photo(name,"seed-$index.png","","","image/png",1,123,"a".repeat(64),sourceBindingKey=fixture.source.key,
                        sourceRemoteId=name,cachedVersion="1",importedMetadata=raw,archived=true)
                    db.photos().insert(photo);db.cloud().savePhoto(CloudPhoto("${fixture.source.key}:$name",fixture.source.key,name,"folder",photo.displayName,
                        "image/png",1,"md5","1",null,null,null,null,photoId=photo.id,sha256=photo.sha256))
                }
            }
            val before=app.metadata.snapshot(fixture.source);val error=assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(before) };assertTrue(error.message.orEmpty().contains("2 MiB"))
            val zip=fixture.zip("metadata");val raw="""{"title":"photo.png","photoTakenTime":{"timestamp":"124"},"people":[{"name":"Synthetic person"}]}"""
            val item=fixture.item("metadata","photo.png",zip,raw);db.imports().insertItem(item);assertTrue(fixture.run())
            val completed=requireNotNull(db.imports().item(item.id));assertEquals("COMPLETE",completed.state);assertEquals(raw,completed.metadata)
            assertArrayEquals(fixture.png,fixture.uploads[completed.remoteId]);val photo=requireNotNull(db.photos().find(requireNotNull(completed.photoId)))
            assertEquals(Fixture.digest(fixture.png,"SHA-256"),photo.sha256);assertTrue(photo.importedMetadata.contains("Synthetic person"))
            val state=requireNotNull(db.metadata().state(fixture.source.key));assertTrue(state.generation>state.uploadedGeneration)
            val expected=before.assets.associateBy { it.id };val after=app.metadata.snapshot(fixture.source)
            assertEquals(expected,after.assets.filter { it.id in expected }.associateBy { it.id });assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(after) }
            assertEquals("COMPLETE",db.imports().job(fixture.job.id)?.state);assertTrue(zip.isFile);assertEquals(1,fixture.starts.get())
            compose.onNodeWithTag("shell:activity").performClick();waitCount(fixture.job.id,"1/1 verified in Drive")
            compose.onNodeWithTag("import-count:${fixture.job.id}").performScrollTo().assertTextEquals("1/1 verified in Drive");screenshot("import-xmp-independent")
        }
    }
    private fun screenshot(name: String) {
        compose.waitForIdle();val drawn=CountDownLatch(1)
        instrumentation.runOnMainSync { val view=compose.activity.window.decorView;view.postInvalidateOnAnimation();view.postOnAnimation { view.postOnAnimation { drawn.countDown() } } }
        assertTrue(drawn.await(5,TimeUnit.SECONDS));instrumentation.waitForIdleSync()
        val bitmap=requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        try {
            val values=android.content.ContentValues().apply {
                put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"$name.png");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
            }
            val uri=requireNotNull(app.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
            requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(bitmap.compress(Bitmap.CompressFormat.PNG,100,it)) }
        } finally { bitmap.recycle() }
    }
}
