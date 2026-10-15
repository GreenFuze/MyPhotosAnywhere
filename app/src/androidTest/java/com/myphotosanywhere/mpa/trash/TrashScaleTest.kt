package com.myphotosanywhere.mpa.trash

import android.os.Build
import androidx.paging.PagingSource
import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class TrashScaleTest {
    @Test fun tenThousandAndHundredThousandTrashEntriesUseBoundedIndexedPages()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val measurements=JSONArray()
        for(count in listOf(10_000,100_000)) {
            val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).allowMainThreadQueries().build()
            try {
                db.drive().saveSource(DriveSource("scale","account","scale@example.test","APP_FILES","root","Synthetic"))
                val database=db.openHelper.writableDatabase
                database.beginTransaction()
                try {
                    database.compileStatement("INSERT INTO drive_trash (key,bindingKey,accountId,remoteId,name,mimeType,bytes,thumbnailPath,confirmed,updatedAt,expectedVersion,observedVersion) VALUES (?,'scale','account',?,?,'image/jpeg',8,'',1,?,'1','1')").use { insert ->
                        repeat(count) { index ->
                            insert.bindString(1,"scale:$index");insert.bindString(2,index.toString())
                            insert.bindString(3,"synthetic-$index.jpg");insert.bindLong(4,(index/2).toLong())
                            insert.executeInsert()
                        }
                    }
                    database.setTransactionSuccessful()
                } finally { database.endTransaction() }
                val queryPlan=database.query("EXPLAIN QUERY PLAN SELECT * FROM drive_trash WHERE confirmed=1 OR desired IS NOT NULL OR error IS NOT NULL ORDER BY updatedAt DESC,key ASC LIMIT 90").use { rows ->
                    buildList { while(rows.moveToNext()) add(rows.getString(3)) }
                }
                assertTrue("Trash must avoid a full sorted materialization: $queryPlan",queryPlan.none { it.contains("TEMP B-TREE",true) })
                val source=TrashPagingSource(db)
                val heapBefore=Runtime.getRuntime().totalMemory()-Runtime.getRuntime().freeMemory()
                val start=System.nanoTime()
                val result=source.load(PagingSource.LoadParams.Refresh(null,90,false))
                check(result is PagingSource.LoadResult.Page)
                val firstMs=(System.nanoTime()-start)/1_000_000.0
                assertEquals(90,result.data.size);assertEquals(90,result.data.map { it.key }.distinct().size)
                val nextStart=System.nanoTime()
                val next=source.load(PagingSource.LoadParams.Append(requireNotNull(result.nextKey),60,false))
                check(next is PagingSource.LoadResult.Page)
                val nextMs=(System.nanoTime()-nextStart)/1_000_000.0
                assertEquals(60,next.data.size);assertEquals(150,(result.data+next.data).map { it.key }.distinct().size)
                measurements.put(JSONObject().put("items",count).put("initialPageMs",firstMs).put("appendPageMs",nextMs)
                    .put("initialRows",90).put("appendRows",60).put("pageBudget",TrashPagingSource.CONFIG.maxSize).put("orderedIndex",true)
                    .put("heapBeforePageBytes",heapBefore).put("heapAfterPageBytes",Runtime.getRuntime().totalMemory()-Runtime.getRuntime().freeMemory()))
                source.invalidate()
            } finally { db.close() }
        }
        val report=JSONObject().put("api",Build.VERSION.SDK_INT).put("measurements",measurements)
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"trash-benchmark.json")
            put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).use { it.write(report.toString(2).toByteArray(Charsets.UTF_8)) }
    }
}
