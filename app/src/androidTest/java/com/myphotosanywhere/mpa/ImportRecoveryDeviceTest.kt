package com.myphotosanywhere.mpa

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.importing.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ImportRecoveryDeviceTest {
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    @Test fun blockedImportSurvivesRecreationAndRecoversWithoutResettingTransfers() = runBlocking<Unit> {
        val name="foreground-recovery-${java.util.UUID.randomUUID()}.db"
        var db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).build()
        val item=ImportItem("item","job","content://source","clip.mp4","clip.mp4","video/mp4",100,
            state="UPLOADING",sha256="sha",md5="md5",remoteId="reserved",sessionUri="session",offset=42)
        try {
            db.imports().saveJob(ImportJob("job","original","TAKEOUT",state="RUNNING")); db.imports().insertItem(item)
            assertTrue(ImportForegroundRecovery(db.imports()).record("job"))
            db.close(); db=Room.databaseBuilder(context,PhotoDatabase::class.java,name).build()
            val queued=mutableListOf<String>()
            repeat(2) { ImportForegroundRecovery(db.imports()).resume({ it=="original" }) { id,replace ->
                assertFalse(replace); queued.add(id)
            } }
            assertEquals(listOf("job"),queued)
            assertEquals("RUNNING",db.imports().job("job")?.state); assertNull(db.imports().job("job")?.error)
            assertEquals(item,db.imports().item("item"))
            if(android.os.Build.VERSION.SDK_INT>=31) {
                assertTrue(ImportForegroundRecovery.isStartRefused(android.app.ForegroundServiceStartNotAllowedException("test")))
            }
        } finally { db.close(); context.deleteDatabase(name) }
    }
    @Test fun nativeSqliteUsesScopedHashIndexForUndownloadedMedia() = runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(context,PhotoDatabase::class.java).build()
        try {
            val sha="a".repeat(64)
            val row=CloudPhoto("source:remote","source","remote","nested","photo.jpg","image/jpeg",4,"md5","1",null,null,null,null,sha256=sha)
            db.cloud().savePhoto(row); db.cloud().savePhoto(row.copy(key="other:remote",bindingKey="other"))
            assertEquals(listOf(row),db.cloud().exactCandidates("source",sha,4,""))
            assertEquals(1,db.cloud().liveCount("source"))
            db.openHelper.readableDatabase.query("EXPLAIN QUERY PLAN SELECT * FROM cloud_photos WHERE bindingKey='source' AND sha256='$sha' AND bytes=4 AND removed=0 AND key>'' ORDER BY key LIMIT 32").use { cursor ->
                val details=mutableListOf<String>()
                while(cursor.moveToNext()) details.add(cursor.getString(3))
                assertTrue(details.any { it.contains("index_cloud_photos_bindingKey_sha256_bytes") })
            }
        } finally { db.close() }
    }
}
