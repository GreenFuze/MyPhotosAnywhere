package com.myphotosanywhere.mpa.timeline

import android.os.Debug
import androidx.room.Room
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** Native SQLite measurements; no live accounts, originals, or production database are touched. */
@RunWith(AndroidJUnit4::class)
class TimelineScaleTest {
    @Test fun benchmarkMixedTenThousandAndHundredThousandItemLibraries()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val results=JSONArray()
        for(size in listOf(10000,100000)) {
            val databaseName="timeline-scale-${java.util.UUID.randomUUID()}.db"
            val database=Room.databaseBuilder(context,PhotoDatabase::class.java,databaseName).build()
            val directory=File(context.noBackupFilesDir,"scale-viewer-$size")
            try {
                database.drive().saveSource(DriveSource("scale","account","scale@test","APP_FILES","folder","Scale"))
                withContext(Dispatchers.IO) { database.withTransaction {
                    val sql=database.openHelper.writableDatabase
                    sql.compileStatement("""INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,capturedAt,sourceBindingKey,sourceRemoteId)
                        VALUES(?,?,'','','image/jpeg',10,1,?,?,'scale',?)""").use { local ->
                        sql.compileStatement("""INSERT INTO cloud_photos(`key`,bindingKey,remoteId,parentId,name,mimeType,bytes,
                            capturedAt,thumbnailPath,removed,seenGeneration,thumbnailUpdatedAt,photoId)
                            VALUES(?,'scale',?,'folder',?,'image/jpeg',10,?,'',0,'fixture',0,?)""").use { cloud ->
                            repeat(size) { index ->
                                val id="scale-${index.toString().padStart(6,'0')}"
                                if(index%2==0) {
                                    local.clearBindings();local.bindString(1,id);local.bindString(2,"$id.jpg")
                                    local.bindString(3,"sha-$id");local.bindLong(4,index.toLong());local.bindString(5,id);local.executeInsert()
                                }
                                cloud.clearBindings();cloud.bindString(1,"scale:$id");cloud.bindString(2,id)
                                cloud.bindString(3,"$id.jpg");cloud.bindLong(4,index.toLong())
                                if(index%2==0) cloud.bindString(5,id) else cloud.bindNull(5)
                                cloud.executeInsert()
                            }
                        }
                    }
                } }
                val repository=TimelineRepository(database)
                val query=TimelineQuery()
                val mosaicStarted=System.nanoTime()
                val mosaic=JustifiedMosaic().plan(List(300) { MosaicInput("bounded-$it",listOf(0.67f,1f,1.5f)[it%3]) },3,407f,GalleryLayout.MOSAIC)
                val mosaicMillis=(System.nanoTime()-mosaicStarted)/1000000.0
                assertEquals(300,mosaic.cells.size);assertEquals(3,mosaic.columns)
                val memoryBefore=Debug.getPss()
                var started=System.nanoTime()
                val first=repository.page(query,null,TimelineDirection.AFTER,90)
                val firstMillis=(System.nanoTime()-started)/1000000.0
                started=System.nanoTime()
                val dateRange=repository.dateRange(query)
                val dateRangeMillis=(System.nanoTime()-started)/1000000.0
                assertEquals(TimelineDateRange(0,size-1L),dateRange)
                assertEquals(90,first.size)
                started=System.nanoTime()
                val middle=repository.seek(query,size/2L)
                val seekMillis=(System.nanoTime()-started)/1000000.0
                assertNotNull(middle)
                started=System.nanoTime()
                val around=repository.around(query,middle,90)
                val aroundMillis=(System.nanoTime()-started)/1000000.0
                assertEquals(90,around.size);assertEquals(90,around.map { it.key }.toSet().size)
                started=System.nanoTime()
                val preparation=async(Dispatchers.IO) { ViewerSessionStore(directory,repository).prepare(query,around[45]) }
                var snapshotImportMillis=0.0
                if(size==100000) {
                    // A written index page proves the source snapshot has begun.
                    withTimeout(30000) {
                        while(directory.listFiles().orEmpty().sumOf { it.length() }<128*1024L && !preparation.isCompleted) delay(5)
                    }
                    assertFalse("Scale fixture must exercise an active source snapshot",preparation.isCompleted)
                    val writeStarted=System.nanoTime()
                    withContext(Dispatchers.IO) {
                        database.openHelper.writableDatabase.execSQL("UPDATE cloud_photos SET capturedAt=? WHERE `key`=?",arrayOf<Any>(size+2,"scale:scale-000003"))
                    }
                    snapshotImportMillis=(System.nanoTime()-writeStarted)/1000000.0
                    assertFalse("A read snapshot must not hold the import writer until indexing finishes",preparation.isCompleted)
                }
                val session=preparation.await()
                val snapshotMillis=(System.nanoTime()-started)/1000000.0
                assertEquals("Snapshot must retain the order before the concurrent import",first.first().key,session.entry(0).key)
                val selected=session.entry(session.initialPage)
                assertEquals(around[45].key,selected.key);assertEquals(size,session.count)
                val import=async(Dispatchers.IO) {
                    database.openHelper.writableDatabase.execSQL("UPDATE cloud_photos SET capturedAt=? WHERE `key`=?",arrayOf<Any>(size+1,"scale:scale-000001"))
                }
                started=System.nanoTime()
                val updated=repository.page(query,null,TimelineDirection.AFTER,60)
                import.await()
                val duringImportMillis=(System.nanoTime()-started)/1000000.0
                assertEquals(60,updated.size)
                assertEquals(selected,session.entry(session.initialPage))
                assertEquals(10,database.openHelper.readableDatabase.version)
                val memoryAfter=Debug.getPss()
                results.put(JSONObject().put("items",size).put("firstPageMs",firstMillis).put("seekMs",seekMillis)
                    .put("dateRangeMs",dateRangeMillis).put("mosaicPlanMs",mosaicMillis).put("aroundPageMs",aroundMillis).put("snapshotMs",snapshotMillis).put("duringImportMs",duringImportMillis).put("snapshotImportMs",snapshotImportMillis)
                    .put("pssBeforeKb",memoryBefore).put("pssAfterKb",memoryAfter)
                    .put("pageBudget",TimelinePagingSource.CONFIG.maxSize).put("pageRows",90)
                    .put("indexBytes",directory.listFiles().orEmpty().sumOf { it.length() }))
                session.close()
                assertTrue(directory.listFiles().orEmpty().isEmpty())
            } finally { database.close();context.deleteDatabase(databaseName);directory.deleteRecursively() }
        }
        val output=JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("measurements",results)
        if(android.os.Build.VERSION.SDK_INT>=29) {
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"timeline-benchmark.json")
            put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).bufferedWriter().use { it.write(output.toString(2)) }
        } else File(requireNotNull(context.getExternalFilesDir(null)),"timeline-benchmark.json").writeText(output.toString(2))
    }
}
