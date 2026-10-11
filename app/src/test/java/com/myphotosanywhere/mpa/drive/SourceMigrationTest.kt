package com.myphotosanywhere.mpa.drive

import android.app.Application
import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.PhotoDatabase
import java.io.File
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class SourceMigrationTest {
    @Test fun upgradingV3PreservesMediaCorrectionsAndSync() = runBlocking {
        val context=RuntimeEnvironment.getApplication()
        val name="sources-migration-${UUID.randomUUID()}.db"
        val binding=DriveBinding("account","folder").key
        val schema=JsonParser.parseString(File("schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/3.json").readText())
            .asJsonObject.getAsJsonObject("database")
        val helper=FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(context)
            .name(name).callback(object: SupportSQLiteOpenHelper.Callback(3) {
                override fun onCreate(db: SupportSQLiteDatabase) {
                    for(entity in schema.getAsJsonArray("entities")) {
                        val table=entity.asJsonObject
                        fun sql(value: String) = value.replace("\${TABLE_NAME}",table.get("tableName").asString)
                        db.execSQL(sql(table.get("createSql").asString))
                        for(index in table.getAsJsonArray("indices") ?: com.google.gson.JsonArray())
                            db.execSQL(sql(index.asJsonObject.get("createSql").asString))
                    }
                    db.execSQL("INSERT INTO drive_connection VALUES (1,'account','owner@example.test','APP_FILES','folder','MPA',1,0)")
                    db.execSQL("INSERT INTO photos (id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256) VALUES ('photo','family.jpg','/original','/thumb','image/jpeg',10,1,'sha')")
                    db.execSQL("INSERT INTO people VALUES ('person','Sam')")
                    db.execSQL("INSERT INTO faces VALUES ('face','photo','/face',NULL,'person',NULL,NULL,1)")
                    db.execSQL("INSERT INTO sync_states VALUES (?,'change-token',123,NULL,0)",arrayOf(binding))
                    db.execSQL("INSERT INTO drive_backups VALUES (?,?, 'photo','account','owner@example.test','folder','APP_FILES','reserved','session','UPLOADING',5,NULL,NULL,NULL)",arrayOf("$binding:photo",binding))
                }
                override fun onUpgrade(db: SupportSQLiteDatabase,oldVersion: Int,newVersion: Int) = error("Room owns migration")
            }).build())
        var db: PhotoDatabase?=null
        try {
            helper.writableDatabase; helper.close()
            val opened=Room.databaseBuilder(context,PhotoDatabase::class.java,name).addMigrations(SOURCES_MIGRATION_3_4, com.myphotosanywhere.mpa.data.METADATA_MIGRATION_4_5, com.myphotosanywhere.mpa.drive.SCAN_MIGRATION_5_6, com.myphotosanywhere.mpa.importing.IMPORT_MIGRATION_6_7)
                .allowMainThreadQueries().build()
            db=opened
            val source=requireNotNull(opened.drive().source(binding))
            assertEquals("MPA",source.folderName); assertTrue(source.backupEnabled); assertFalse(source.unmeteredOnly)
            assertEquals(binding,opened.drive().backupDestination()?.key)
            assertEquals("/original",opened.photos().find("photo")?.originalPath)
            assertEquals("person",opened.library().face("face")?.personId)
            assertTrue(opened.library().face("face")?.corrected==true)
            assertEquals("change-token",opened.cloud().sync(binding)?.changeToken)
            assertEquals("session",opened.drive().backup("$binding:photo")?.sessionUri)
            assertEquals(5L,opened.drive().backup("$binding:photo")?.uploadedBytes)
        } finally { db?.close(); helper.close(); context.deleteDatabase(name) }
    }
}
