package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import androidx.sqlite.db.*
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.google.gson.*
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportMigrationTest {
    @Test fun migrateV6() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication(); val name="imig-${java.util.UUID.randomUUID()}.db"
        val schema=JsonParser.parseString(File("schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/6.json").readText()).asJsonObject.getAsJsonObject("database")
        val helper=FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(context).name(name)
            .callback(object: SupportSQLiteOpenHelper.Callback(6) {
                override fun onCreate(db: SupportSQLiteDatabase) {
                    for(entity in schema.getAsJsonArray("entities")) {
                        val table=entity.asJsonObject
                        fun sql(value: String)=value.replace("\${TABLE_NAME}",table.get("tableName").asString)
                        db.execSQL(sql(table.get("createSql").asString))
                        for(index in table.getAsJsonArray("indices") ?: JsonArray()) db.execSQL(sql(index.asJsonObject.get("createSql").asString))
                    }
                    db.execSQL("INSERT INTO photos(id,displayName,originalPath,thumbnailPath,mimeType,bytes,importedAt,sha256,metadataAssetId) VALUES('photo','p.jpg','original','thumb','image/jpeg',4,1,'sha','portable')")
                    db.execSQL("INSERT INTO people VALUES('person','Sam')")
                    db.execSQL("INSERT INTO faces(id,photoId,cropPath,personId,corrected) VALUES('face','photo','crop','person',1)")
                    db.execSQL("INSERT INTO drive_sources VALUES('binding','account','test@example.test','EXISTING_LIBRARY','folder','Test',1,1,1)")
                    db.execSQL("INSERT INTO drive_backups VALUES('upload','binding','photo','account','test@example.test','folder','EXISTING_LIBRARY','reserved','session','UPLOADING',2,NULL,NULL,NULL)")
                    db.execSQL("INSERT INTO sync_states(bindingKey,changeToken,lastScan,running,scanGeneration,scanToken,scanPages) VALUES('binding','changes',123,0,'generation','before',7)")
                    db.execSQL("INSERT INTO scan_folders VALUES('binding','nested','page',0)")
                }
                override fun onUpgrade(db: SupportSQLiteDatabase,oldVersion: Int,newVersion: Int)=error("Room owns migration")
            }).build())
        var db: PhotoDatabase?=null
        try {
            helper.writableDatabase; helper.close()
            val opened=Room.databaseBuilder(context,PhotoDatabase::class.java,name).addMigrations(IMPORT_MIGRATION_6_7, TAKEOUT_DOWNLOAD_MIGRATION_7_8, com.myphotosanywhere.mpa.drive.HASH_INDEX_MIGRATION_8_9, com.myphotosanywhere.mpa.trash.TRASH_MIGRATION_9_10).allowMainThreadQueries().build()
            db=opened
            assertEquals("portable",opened.photos().find("photo")?.metadataAssetId)
            assertEquals("",opened.photos().find("photo")?.importedMetadata)
            assertEquals("person",opened.library().face("face")?.personId); assertTrue(opened.library().face("face")?.corrected==true)
            assertEquals("session",opened.drive().backup("upload")?.sessionUri); assertEquals(2L,opened.drive().backup("upload")?.uploadedBytes)
            assertEquals("generation",opened.cloud().sync("binding")?.scanGeneration); assertEquals(7,opened.cloud().sync("binding")?.scanPages)
            assertEquals("page",opened.cloud().nextScanFolder("binding")?.pageToken)
            opened.imports().saveJob(ImportJob("job","binding","TAKEOUT"))
            opened.imports().insertItem(ImportItem("item","job","uri","p.jpg","p.jpg","image/jpeg",4))
            assertEquals("binding",opened.imports().job("job")?.binding)
            val oldGeneration=opened.metadata().state("binding")!!.generation
            opened.photos().update(opened.photos().find("photo")!!.copy(importedMetadata="{\"kept\":true}"))
            assertTrue(opened.metadata().state("binding")!!.generation>oldGeneration)
        } finally { db?.close(); helper.close(); context.deleteDatabase(name) }
    }
}
