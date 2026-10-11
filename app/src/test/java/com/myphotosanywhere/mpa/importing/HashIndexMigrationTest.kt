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
class HashIndexMigrationTest {
    @Test fun migrateV8PreservesWorkAndOnlyBackfillsVerifiedScopedIdentities() = runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication(); val name="imig-${java.util.UUID.randomUUID()}.db"
        val schema=JsonParser.parseString(File("schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/8.json").readText()).asJsonObject.getAsJsonObject("database")
        val helper=FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(context).name(name)
            .callback(object: SupportSQLiteOpenHelper.Callback(8) {
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
                    db.execSQL("INSERT INTO import_jobs VALUES('existing-job','binding','TAKEOUT','',NULL,NULL,'PAUSED',NULL,1)")
                    db.execSQL("INSERT INTO import_items VALUES('existing-item','existing-job','content://archive','p.jpg','p.jpg','image/jpeg',4,7,NULL,'Album',1,'UPLOADING',NULL,'sha','md5','/stage','reserved','upload-session',2,NULL)")
                    db.execSQL("INSERT INTO scan_folders VALUES('binding','nested','page',0)")
                    db.execSQL("UPDATE photos SET sha256=?,sourceBindingKey='binding',sourceRemoteId='remote',cachedVersion='1' WHERE id='photo'",arrayOf("a".repeat(64)))
                    for((key,binding,version) in listOf(Triple("binding:remote","binding","1"),Triple("binding:stale","binding","2"),Triple("other:remote","other","1"))) {
                        db.execSQL("""INSERT INTO cloud_photos(key,bindingKey,remoteId,parentId,name,mimeType,bytes,md5,version,thumbnailPath,photoId,removed,seenGeneration,thumbnailUpdatedAt)
                            VALUES(?,?,'remote','folder','p.jpg','image/jpeg',4,'md5',?,'thumb','photo',0,'generation',0)""",arrayOf(key,binding,version))
                    }
                    db.execSQL("INSERT INTO sync_states(bindingKey,changeToken,lastScan,lastSuccess,running) VALUES('idle','idle-token',999,888,0)")
                    db.execSQL("INSERT INTO takeout_downloads VALUES('existing-job','encrypted-link',123,456,NULL,'READY',0,'old error')")
                    com.myphotosanywhere.mpa.metadata.installMetadataTriggers(db)
                }
                override fun onUpgrade(db: SupportSQLiteDatabase,oldVersion: Int,newVersion: Int)=error("Room owns migration")
            }).build())
        var db: PhotoDatabase?=null
        try {
            helper.writableDatabase; helper.close()
            val opened=Room.databaseBuilder(context,PhotoDatabase::class.java,name).addMigrations(com.myphotosanywhere.mpa.drive.HASH_INDEX_MIGRATION_8_9).allowMainThreadQueries().build()
            db=opened
            assertEquals(0L,opened.cloud().sync("idle")?.lastScan)
            assertEquals(888L,opened.cloud().sync("idle")?.lastSuccess)
            assertEquals("idle-token",opened.cloud().sync("idle")?.changeToken)
            assertEquals(123L,opened.cloud().sync("binding")?.lastScan)
            assertEquals("a".repeat(64),opened.cloud().photo("binding:remote")?.sha256)
            assertNull(opened.cloud().photo("binding:stale")?.sha256)
            assertNull(opened.cloud().photo("other:remote")?.sha256)
            assertEquals(listOf("binding:remote"),opened.cloud().exactCandidates("binding","a".repeat(64),4,"").map { it.key })
            assertEquals("old error",opened.imports().download("existing-job")?.error)
            assertEquals(123L,opened.imports().download("existing-job")?.bytes)
            assertEquals("PAUSED",opened.imports().job("existing-job")?.state)
            assertEquals("upload-session",opened.imports().item("existing-item")?.sessionUri)
            assertEquals(2L,opened.imports().item("existing-item")?.offset)
            val checkpoint=TakeoutDownload("existing-job","encrypted-link",123,456,"\"revision\"",autoStart=false)
            opened.imports().saveDownload(checkpoint)
            assertEquals(checkpoint,opened.imports().download("existing-job"))
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
            opened.close()
            val reopened=Room.databaseBuilder(context,PhotoDatabase::class.java,name).allowMainThreadQueries().build()
            db=reopened
            assertEquals(checkpoint,reopened.imports().download("existing-job"))
            assertEquals("upload-session",reopened.imports().item("existing-item")?.sessionUri)
        } finally { db?.close(); helper.close(); context.deleteDatabase(name) }
    }
}
