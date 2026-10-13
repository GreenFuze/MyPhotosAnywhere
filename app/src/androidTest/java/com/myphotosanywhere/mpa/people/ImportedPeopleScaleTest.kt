package com.myphotosanywhere.mpa.people

import android.os.Debug
import androidx.room.Room
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

@RunWith(AndroidJUnit4::class)
class ImportedPeopleScaleTest {
    @Test fun importedLabelsRemainBoundedAndExactWithTenThousandAndHundredThousandItems()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext;val measurements=JSONArray()
        for(size in listOf(10000,100000)) {
            val name="takeout-people-scale-${java.util.UUID.randomUUID()}.db";val db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).build()
            val directory=File(context.noBackupFilesDir,"takeout-people-scale-$size")
            try {
                val metadata="""{"googleTakeout":{"title":"photo.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Exported name"}]}}"""
                withContext(Dispatchers.IO) { db.withTransaction {
                    db.openHelper.writableDatabase.compileStatement("""INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,capturedAt,importedMetadata)
                        VALUES(?,'photo.jpg','','','image/jpeg',10,1,?,?,?)""").use { insert ->
                        repeat(size) { index -> val id="imported-scale-${index.toString().padStart(6,'0')}";insert.clearBindings();insert.bindString(1,id);insert.bindString(2,"sha-$id");insert.bindLong(3,index.toLong());insert.bindString(4,if(index%100==0) metadata else "");insert.executeInsert() }
                    }
                } }
                val repository=TimelineRepository(db);val imported=ImportedPeopleRepository(repository);val before=Debug.getPss();var start=System.nanoTime()
                val groups=imported.groups();val groupsMs=(System.nanoTime()-start)/1e6;assertEquals(1,groups.size);assertEquals(size/100,groups.single().photos)
                val query=TimelineQuery(person=groups.single().label.key);start=System.nanoTime();val page=repository.page(query,null,TimelineDirection.AFTER,60);val firstMs=(System.nanoTime()-start)/1e6
                assertEquals(60,page.size);assertTrue(page.all { it.searchDocument==null });start=System.nanoTime()
                val session=ViewerSessionStore(directory,repository).prepare(query,page[30]);val snapshotMs=(System.nanoTime()-start)/1e6
                try { assertEquals(size/100,session.count);assertEquals(page[30].key,session.entry(session.initialPage).key)
                    measurements.put(JSONObject().put("items",size).put("groupsMs",groupsMs).put("firstPageMs",firstMs).put("snapshotMs",snapshotMs).put("pssBeforeKb",before).put("pssAfterKb",Debug.getPss()).put("pageBudget",TimelinePagingSource.CONFIG.maxSize).put("retainedMetadataDocuments",0).put("snapshotItems",session.count))
                } finally { session.close() }
                assertEquals(9,db.openHelper.readableDatabase.version)
            } finally { db.close();context.deleteDatabase(name);directory.deleteRecursively() }
        }
        val values=android.content.ContentValues().apply { put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"takeout-people-benchmark.json");put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json");put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA") }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values));requireNotNull(context.contentResolver.openOutputStream(uri)).bufferedWriter().use { it.write(JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("measurements",measurements).toString(2)) }
    }
}
