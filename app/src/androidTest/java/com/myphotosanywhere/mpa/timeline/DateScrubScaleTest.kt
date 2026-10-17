package com.myphotosanywhere.mpa.timeline

import android.os.Debug
import androidx.room.Room
import androidx.room.withTransaction
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant
import java.time.ZoneId

/** Calendar-spanning native fixtures supplement the existing import/snapshot scale benchmark. */
@RunWith(AndroidJUnit4::class)
class DateScrubScaleTest {
    @Test fun benchmarkDateSeeksTenThousandAndHundredThousandItemsDuringImport()=runBlocking<Unit> {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val measurements=JSONArray();val base=Instant.parse("2000-01-01T12:00:00Z").toEpochMilli();val day=86400000L
        for(size in listOf(10000,100000)) {
            val name="date-scrub-scale-${java.util.UUID.randomUUID()}.db"
            val database=Room.databaseBuilder(context,PhotoDatabase::class.java,name).build()
            try {
                withContext(Dispatchers.IO) { database.withTransaction {
                    database.openHelper.writableDatabase.compileStatement("""INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,capturedAt)
                        VALUES(?,?,'','','image/jpeg',10,1,?,?)""").use { insert ->
                        repeat(size) { index ->
                            val id="date-${index.toString().padStart(6,'0')}"
                            insert.clearBindings();insert.bindString(1,id);insert.bindString(2,"$id.jpg");insert.bindString(3,id)
                            insert.bindLong(4,base+index*day);insert.executeInsert()
                        }
                    }
                } }
                val repository=TimelineRepository(database);val query=TimelineQuery();val before=Debug.getPss()
                var started=System.nanoTime();val dates=requireNotNull(repository.dateRange(query));val rangeMs=(System.nanoTime()-started)/1000000.0
                assertEquals(TimelineDateRange(base,base+(size-1L)*day),dates)
                val scale=DateScrubScale(dates.months(ZoneId.of("UTC")))
                val times=JSONArray();var maximumRows=0
                for(index in 0..20) {
                    val month=scale.month(index/20f)
                    val until=month.atEndOfMonth().plusDays(1).atStartOfDay(ZoneId.of("UTC")).toInstant().toEpochMilli()
                    started=System.nanoTime();val cursor=requireNotNull(repository.seek(query,until))
                    val elapsed=(System.nanoTime()-started)/1000000.0
                    val expected=minOf(size-1L,(until-1-base)/day)
                    assertEquals("photo:date-${expected.toString().padStart(6,'0')}",cursor.key)
                    val page=repository.around(query,cursor,90);maximumRows=maxOf(maximumRows,page.size)
                    assertTrue(page.size<=90);assertEquals(page.size,page.map { it.key }.toSet().size)
                    assertTrue(page.any { it.key==cursor.key });times.put(elapsed)
                }
                val focused=requireNotNull(repository.seek(query,base+size/2*day))
                val write=async(Dispatchers.IO) {
                    database.openHelper.writableDatabase.execSQL("UPDATE photos SET capturedAt=? WHERE id=?",arrayOf<Any>(base+size*day,"date-000000"))
                }
                started=System.nanoTime();val during=repository.seek(query,requireNotNull(focused.millis)+1);write.await()
                val duringMs=(System.nanoTime()-started)/1000000.0
                assertEquals(focused,during);assertEquals(11,database.openHelper.readableDatabase.version)
                measurements.put(JSONObject().put("items",size).put("dateRangeMs",rangeMs).put("seekMs",times).put("seekDuringImportMs",duringMs)
                    .put("maximumRetainedRows",maximumRows).put("configuredPageBudget",TimelinePagingSource.CONFIG.maxSize)
                    .put("pssBeforeKb",before).put("pssAfterKb",Debug.getPss()))
            } finally { database.close();check(context.deleteDatabase(name)) }
        }
        val output=JSONObject().put("api",android.os.Build.VERSION.SDK_INT).put("measurements",measurements)
        val values=android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,"date-scrub-benchmark.json")
            put(android.provider.MediaStore.MediaColumns.MIME_TYPE,"application/json")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,"Download/MPA")
        }
        val uri=requireNotNull(context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI,values))
        requireNotNull(context.contentResolver.openOutputStream(uri)).bufferedWriter().use { it.write(output.toString(2)) }
    }
}
