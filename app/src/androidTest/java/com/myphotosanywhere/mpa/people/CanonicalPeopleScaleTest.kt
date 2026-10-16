package com.myphotosanywhere.mpa.people

import android.os.Debug
import androidx.room.Room
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.*
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** Indexed-reference fixture, not a claim about physical MediaStore or frame-rate performance. */
@RunWith(AndroidJUnit4::class)
class CanonicalPeopleScaleTest {
    @Test fun deviceReferencesAndCanonicalPeopleRemainPagedAtTenAndHundredThousandItems()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val results=JSONArray()
        for(size in listOf(10000,100000)) {
            val name="canonical-scale-${java.util.UUID.randomUUID()}.db"
            val db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).addCallback(object: androidx.room.RoomDatabase.Callback() {
                override fun onCreate(db: androidx.sqlite.db.SupportSQLiteDatabase) { installPersonLabelTriggers(db) }
            }).build()
            val directory=File(context.noBackupFilesDir,"canonical-scale-$size")
            try {
                withContext(Dispatchers.IO) { db.withTransaction {
                    val database=db.openHelper.writableDatabase
                    database.compileStatement("""INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,capturedAt,importedMetadata)
                        VALUES(?,'photo.jpg','','content://media/external/file/fixture','image/jpeg',10,1,'',?,?)""").use { photo ->
                        database.compileStatement("""INSERT INTO device_media(photoId,uri,volume,providerVersion,revision,width,height,orientation,available)
                            VALUES(?,'content://media/external/file/fixture','external','fixture','[10,1,1,1,null,"image/jpeg",120,80,0]',120,80,0,1)""").use { device ->
                            repeat(size) { index ->
                                val id="device-scale-${index.toString().padStart(6,'0')}"
                                val metadata=if(index%20==0) """{"googleTakeout":{"title":"photo.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Person ${(index/20)%100}"}]}}""" else ""
                                photo.clearBindings();photo.bindString(1,id);photo.bindLong(2,index.toLong());photo.bindString(3,metadata);photo.executeInsert()
                                device.clearBindings();device.bindString(1,id);device.executeInsert()
                            }
                        }
                    }
                } }
                val pssBefore=Debug.getPss();var start=System.nanoTime()
                assertEquals(size/20,CanonicalPeopleIndex(db).drain())
                val indexMs=(System.nanoTime()-start)/1e6
                val timeline=TimelineRepository(db)
                start=System.nanoTime();val groups=CanonicalPeopleRepository(timeline).groups();val groupsMs=(System.nanoTime()-start)/1e6
                assertEquals(100,groups.size);assertEquals(size/20,groups.sumOf { it.photos })
                start=System.nanoTime();val first=timeline.page(TimelineQuery(),null,TimelineDirection.AFTER,60);val firstMs=(System.nanoTime()-start)/1e6
                assertEquals(60,first.size);assertTrue(first.all { it.searchDocument==null && it.sha256.isNullOrBlank() })
                val older=timeline.page(TimelineQuery(),first.last().cursor,TimelineDirection.AFTER,60)
                assertEquals(60,older.size);assertTrue(first.map { it.key }.intersect(older.map { it.key }.toSet()).isEmpty())
                assertEquals(first.map { it.key },timeline.page(TimelineQuery(),older.first().cursor,TimelineDirection.BEFORE,60).map { it.key })
                start=System.nanoTime();val matches=timeline.page(TimelineQuery(person=groups.first().person.id),null,TimelineDirection.AFTER,60);val personMs=(System.nanoTime()-start)/1e6
                assertEquals(minOf(60,size/2000),matches.size)
                start=System.nanoTime();val session=ViewerSessionStore(directory,timeline).prepare(TimelineQuery(),first[30]);val snapshotMs=(System.nanoTime()-start)/1e6
                session.use { assertEquals(size,it.count);assertEquals(first[30].key,it.entry(it.initialPage).key) }
                assertEquals(300,TimelinePagingSource.CONFIG.maxSize);assertEquals(11,db.openHelper.readableDatabase.version)
                results.put(JSONObject().put("items",size).put("labelIndexMs",indexMs).put("peopleOverviewMs",groupsMs)
                    .put("firstPageMs",firstMs).put("personPageMs",personMs).put("snapshotMs",snapshotMs)
                    .put("pssBeforeKb",pssBefore).put("pssAfterKb",Debug.getPss()).put("retainedPageBudget",300)
                    .put("fixture","indexed device references; no original copies or physical MediaStore throughput"))
            } finally { db.close();context.deleteDatabase(name);directory.deleteRecursively() }
        }
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"canonical-people-benchmark.json")
            put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).bufferedWriter().use { it.write(JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("measurements",results).toString(2)) }
    }
}
