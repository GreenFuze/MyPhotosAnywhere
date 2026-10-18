package com.myphotosanywhere.mpa.drive

import android.app.Application
import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.google.gson.JsonArray
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = Application::class)
class ScanMigrationTest {
    @Test fun releasedV5UpgradePreservesMediaNamesUploadAndChangeCursor() = runBlocking<Unit> {
        val context = RuntimeEnvironment.getApplication()
        val name = "scan-migration-${UUID.randomUUID()}.db"
        val schema = JsonParser.parseString(File("schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/5.json").readText())
            .asJsonObject.getAsJsonObject("database")
        val helper = FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(context)
            .name(name).callback(object : SupportSQLiteOpenHelper.Callback(5) {
                override fun onCreate(db: SupportSQLiteDatabase) {
                    for (entity in schema.getAsJsonArray("entities")) {
                        val table = entity.asJsonObject
                        fun sql(value: String) = value.replace("\${TABLE_NAME}", table.get("tableName").asString)
                        db.execSQL(sql(table.get("createSql").asString))
                        for (index in table.getAsJsonArray("indices") ?: JsonArray())
                            db.execSQL(sql(index.asJsonObject.get("createSql").asString))
                    }
                    db.execSQL("INSERT INTO photos (id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,metadataAssetId) VALUES ('photo','family.jpg','/original','/thumb','image/jpeg',8,1,'sha','portable-id')")
                    db.execSQL("INSERT INTO people VALUES ('person','Sam')")
                    db.execSQL("INSERT INTO faces (id,photoId,cropPath,personId,corrected) VALUES ('face','photo','/crop','person',1)")
                    db.execSQL("INSERT INTO drive_sources VALUES ('binding','account','test@example.test','EXISTING_LIBRARY','root','Test',1,1,1)")
                    db.execSQL("INSERT INTO drive_backups VALUES ('upload','binding','photo','account','test@example.test','root','EXISTING_LIBRARY','reserved','session','UPLOADING',5,NULL,NULL,NULL)")
                    db.execSQL("INSERT INTO sync_states VALUES ('binding','cursor',123,NULL,1)")
                }
                override fun onUpgrade(db: SupportSQLiteDatabase, oldVersion: Int, newVersion: Int) = error("Room owns migration")
            }).build())
        var db: PhotoDatabase? = null
        try {
            helper.writableDatabase; helper.close()
            val opened = Room.databaseBuilder(context, PhotoDatabase::class.java, name)
                .addMigrations(SCAN_MIGRATION_5_6, com.myphotosanywhere.mpa.importing.IMPORT_MIGRATION_6_7, com.myphotosanywhere.mpa.importing.TAKEOUT_DOWNLOAD_MIGRATION_7_8, com.myphotosanywhere.mpa.drive.HASH_INDEX_MIGRATION_8_9, com.myphotosanywhere.mpa.trash.TRASH_MIGRATION_9_10, com.myphotosanywhere.mpa.data.LIBRARY_FOLLOWUP_MIGRATION_10_11, com.myphotosanywhere.mpa.data.DEVICE_STORAGE_MIGRATION_11_12).allowMainThreadQueries().build()
            db = opened
            val state = requireNotNull(opened.cloud().sync("binding"))
            assertEquals("cursor", state.changeToken)
            assertEquals(0L, state.lastScan) // Hash-index upgrade requests a bounded metadata rescan.
            assertFalse(state.running)
            assertEquals("WAITING", state.status)
            assertEquals(0L, state.lastSuccess) // Old full-scan time was not a complete change replay.
            assertNull(state.scanGeneration)
            assertEquals(0, opened.cloud().scanFolderCount("binding"))
            assertEquals("portable-id", opened.photos().find("photo")?.metadataAssetId)
            assertEquals("/original", opened.photos().find("photo")?.originalPath)
            assertEquals("person", opened.library().face("face")?.personId)
            assertTrue(opened.library().face("face")?.corrected == true)
            assertEquals("session", opened.drive().backup("upload")?.sessionUri)
            assertEquals("reserved", opened.drive().backup("upload")?.remoteId)
            assertEquals(5L, opened.drive().backup("upload")?.uploadedBytes)
            assertEquals("binding", opened.drive().backupDestination()?.key)
            opened.cloud().enqueueScanFolder(ScanFolder("binding", "root", "next"))
            assertEquals("next", opened.cloud().nextScanFolder("binding")?.pageToken)
        } finally {
            db?.close(); helper.close(); context.deleteDatabase(name)
        }
    }
}
