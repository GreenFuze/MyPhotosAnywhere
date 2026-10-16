package com.myphotosanywhere.mpa.device

import android.app.Application
import androidx.room.Room
import androidx.sqlite.db.*
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.data.PhotoDatabase
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class LibraryFollowupMigrationTest {
    @Test fun v10RowsAndBlobsSurviveAdditiveMigration() {
        val context=RuntimeEnvironment.getApplication();val name="lf-${java.util.UUID.randomUUID()}.db"
        val schema=JsonParser.parseString(File("schemas/com.myphotosanywhere.mpa.data.PhotoDatabase/10.json").readText()).asJsonObject.getAsJsonObject("database")
        val tables=schema.getAsJsonArray("entities").map { it.asJsonObject }
        val helper=FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(context).name(name)
            .callback(object: SupportSQLiteOpenHelper.Callback(10) {
                override fun onCreate(db: SupportSQLiteDatabase) {
                    tables.forEach { table ->
                        val tableName=table.get("tableName").asString
                        fun sql(value: String)=value.replace("\${TABLE_NAME}",tableName)
                        db.execSQL(sql(table.get("createSql").asString))
                        (table.getAsJsonArray("indices") ?: com.google.gson.JsonArray()).forEach { db.execSQL(sql(it.asJsonObject.get("createSql").asString)) }
                        val fields=table.getAsJsonArray("fields").map { it.asJsonObject }
                        val columns=fields.joinToString(",") { "`${it.get("columnName").asString}`" }
                        val values=fields.map { field -> when(field.get("affinity").asString) {
                            "INTEGER" -> 7L;"REAL" -> 1.25;"BLOB" -> byteArrayOf(1,2,3);else -> "synthetic-${field.get("columnName").asString}"
                        } }.toTypedArray()
                        db.execSQL("INSERT INTO `$tableName` ($columns) VALUES (${fields.joinToString(",") { "?" }})",values)
                    }
                }
                override fun onUpgrade(db: SupportSQLiteDatabase,oldVersion: Int,newVersion: Int)=error("Unexpected helper upgrade")
            }).build())
        fun snapshot(db: SupportSQLiteDatabase)=tables.associate { table ->
            val tableName=table.get("tableName").asString
            tableName to db.query("SELECT * FROM `$tableName`").use { cursor ->
                val result=ArrayList<List<String?>>()
                while(cursor.moveToNext()) result.add((0 until cursor.columnCount).map { i ->
                    if(cursor.isNull(i)) null else if(cursor.getType(i)==android.database.Cursor.FIELD_TYPE_BLOB) cursor.getBlob(i).joinToString(",") else cursor.getString(i)
                })
                result
            }
        }
        try {
            val before=snapshot(helper.writableDatabase);helper.close()
            val opened=Room.databaseBuilder(context,PhotoDatabase::class.java,name).addMigrations(com.myphotosanywhere.mpa.data.LIBRARY_FOLLOWUP_MIGRATION_10_11).allowMainThreadQueries().build()
            try {
                val database=opened.openHelper.writableDatabase
                assertEquals(11,database.version);assertEquals(before,snapshot(database))
                for(table in listOf("device_media","device_scan_seen","manual_device_backups","person_aliases","photo_people","photo_people_origins"))
                    database.query("SELECT COUNT(*) FROM `$table`").use { assertTrue(it.moveToFirst());assertEquals(0,it.getInt(0)) }
                database.query("SELECT COUNT(*) FROM people_index_queue").use { assertTrue(it.moveToFirst());assertEquals(1,it.getInt(0)) }
            } finally { opened.close() }
        } finally { helper.close();context.deleteDatabase(name) }
    }
}
