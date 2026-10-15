package com.myphotosanywhere.mpa.search

import android.os.Debug
import androidx.room.Room
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveSource
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

@RunWith(AndroidJUnit4::class)
class SearchScaleTest {
    @Test fun benchmarkSparseSearchAndViewerWithTenThousandAndHundredThousandItems()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext;val measurements=JSONArray()
        for(size in listOf(10000,100000)) {
            val name="search-scale-${java.util.UUID.randomUUID()}.db"
            val db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).build()
            val directory=File(context.noBackupFilesDir,"search-scale-$size")
            try {
                db.drive().saveSource(DriveSource("search-scale","account","scale@test","APP_FILES","folder","Search scale"))
                withContext(Dispatchers.IO) { db.withTransaction {
                    db.openHelper.writableDatabase.compileStatement("""INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,capturedAt,sourceBindingKey,labels)
                        VALUES(?,?,'','','image/jpeg',10,1,?,?,'search-scale',?)""").use { insert ->
                        repeat(size) { index ->
                            val id="search-scale-${index.toString().padStart(6,'0')}";insert.clearBindings()
                            insert.bindString(1,id);insert.bindString(2,"$id.jpg");insert.bindString(3,"sha-$id");insert.bindLong(4,index.toLong())
                            insert.bindString(5,if(index%100==0) "needle" else "ordinary");insert.executeInsert()
                        }
                    }
                } }
                val repository=TimelineRepository(db);val query=TimelineQuery(search="needle")
                val before=Debug.getPss();var start=System.nanoTime()
                val first=repository.page(query,null,TimelineDirection.AFTER,60);val firstMs=(System.nanoTime()-start)/1e6
                assertEquals(60,first.size);assertTrue(first.all { it.searchDocument==null })
                start=System.nanoTime();val none=repository.page(TimelineQuery(search="absent-token"),null,TimelineDirection.AFTER,60)
                val noMatchMs=(System.nanoTime()-start)/1e6;assertTrue(none.isEmpty())
                start=System.nanoTime();val session=ViewerSessionStore(directory,repository).prepare(query,first[30]);val snapshotMs=(System.nanoTime()-start)/1e6
                try {
                    assertEquals(size/100,session.count);assertEquals(first[30].key,session.entry(session.initialPage).key)
                    val writer=async(Dispatchers.IO) { db.openHelper.writableDatabase.execSQL("UPDATE photos SET labels='needle' WHERE id='search-scale-000001'") }
                    start=System.nanoTime();val during=repository.page(query,null,TimelineDirection.AFTER,60);writer.await()
                    val importMs=(System.nanoTime()-start)/1e6;assertEquals(60,during.size)
                    measurements.put(JSONObject().put("items",size).put("firstPageMs",firstMs).put("noMatchMs",noMatchMs)
                        .put("snapshotMs",snapshotMs).put("duringImportMs",importMs).put("pssBeforeKb",before).put("pssAfterKb",Debug.getPss())
                        .put("pageBudget",TimelinePagingSource.CONFIG.maxSize).put("retainedMetadataDocuments",0).put("snapshotItems",session.count))
                } finally { session.close() }
                assertEquals(10,db.openHelper.readableDatabase.version)
            } finally { db.close();context.deleteDatabase(name);directory.deleteRecursively() }
        }
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"search-benchmark.json");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).bufferedWriter().use {
            it.write(JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("measurements",measurements).toString(2))
        }
    }
}
