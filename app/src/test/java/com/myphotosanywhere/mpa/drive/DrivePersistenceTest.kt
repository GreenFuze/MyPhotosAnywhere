package com.myphotosanywhere.mpa.drive

import android.app.Application
import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.myphotosanywhere.mpa.data.PhotoDatabase
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = Application::class)
class DrivePersistenceTest {
    private lateinit var name: String
    private var database: PhotoDatabase? = null

    @Before fun legacyDatabase() {
        name = "migration-${UUID.randomUUID()}.db"
        val helper = FrameworkSQLiteOpenHelperFactory().create(SupportSQLiteOpenHelper.Configuration.builder(RuntimeEnvironment.getApplication())
            .name(name).callback(object : SupportSQLiteOpenHelper.Callback(1) {
                override fun onCreate(db: SupportSQLiteDatabase) {
                    db.execSQL("""CREATE TABLE photos (id TEXT NOT NULL, displayName TEXT NOT NULL,
                        originalPath TEXT NOT NULL, thumbnailPath TEXT NOT NULL, mimeType TEXT NOT NULL,
                        bytes INTEGER NOT NULL, importedAt INTEGER NOT NULL, sha256 TEXT NOT NULL,
                        latitude REAL, longitude REAL, parentId TEXT, editProvider TEXT, editInstructions TEXT,
                        PRIMARY KEY(id))""".trimIndent())
                    db.execSQL("CREATE INDEX index_photos_sha256 ON photos(sha256)")
                    db.execSQL("CREATE INDEX index_photos_parentId ON photos(parentId)")
                    db.execSQL("""CREATE TABLE handoffs (id TEXT NOT NULL, photoId TEXT NOT NULL,
                        provider TEXT NOT NULL, instructions TEXT NOT NULL, createdAt INTEGER NOT NULL, PRIMARY KEY(id))""".trimIndent())
                    db.execSQL("""INSERT INTO photos VALUES ('original', 'family.jpg', '/private/original', '/private/thumb',
                        'image/jpeg', 10, 1, 'hash', 32.0, 34.0, NULL, NULL, NULL)""".trimIndent())
                    db.execSQL("INSERT INTO handoffs VALUES ('handoff', 'original', 'CHATGPT', 'Remove the lamp', 1)")
                }
                override fun onUpgrade(db: SupportSQLiteDatabase, oldVersion: Int, newVersion: Int) = error("Room owns the migration")
            }).build())
        helper.writableDatabase
        helper.close()
    }

    private fun open(): PhotoDatabase = Room.databaseBuilder(RuntimeEnvironment.getApplication(), PhotoDatabase::class.java, name)
        .addMigrations(DRIVE_MIGRATION_1_2).allowMainThreadQueries().build().also { database = it }

    @After fun cleanup() {
        database?.close()
        RuntimeEnvironment.getApplication().deleteDatabase(name)
    }

    @Test fun migrationPreservesOriginalsHandoffsAndUploadCheckpointAcrossRestart() = runBlocking {
        var db = open()
        assertEquals("family.jpg", db.photos().find("original")?.displayName)
        assertEquals("/private/original", db.photos().find("original")?.originalPath)
        assertEquals("Remove the lamp", db.photos().findHandoff("handoff")?.instructions)
        val connection = DriveConnection(accountId = "account", email = "owner@example.test", mode = DriveMode.APP_FILES.name,
            folderId = "folder", folderName = "MPA", backupEnabled = true)
        db.drive().saveConnection(connection)
        val binding = requireNotNull(connection.binding())
        val task = DriveBackup("${binding.key}:original", binding.key, "original", "account", "owner@example.test", "folder",
            DriveMode.APP_FILES.name, remoteId = "reserved-id", sessionUri = "session-uri", state = "UPLOADING", uploadedBytes = 5)
        db.drive().insertBackup(task)
        db.close(); db = open()
        assertEquals(connection, db.drive().connection())
        assertEquals(task, db.drive().backup(task.id))
        // Scheduling the same asset again must never replace its reserved ID or acknowledged offset.
        db.drive().insertBackup(task.copy(remoteId = null, sessionUri = null, uploadedBytes = 0))
        assertEquals(task, db.drive().backup(task.id))
    }

    @Test fun changingDestinationDoesNotRewriteOldBackupTasks() = runBlocking {
        val db = open()
        val old = DriveConnection(accountId = "account", email = "owner@example.test", mode = DriveMode.APP_FILES.name,
            folderId = "old-folder", backupEnabled = true)
        val binding = requireNotNull(old.binding())
        val task = DriveBackup("${binding.key}:original", binding.key, "original", old.accountId, old.email,
            requireNotNull(old.folderId), old.mode, remoteId = "reserved-id")
        db.drive().saveConnection(old); db.drive().insertBackup(task)
        db.drive().saveConnection(old.copy(folderId = "new-folder", backupEnabled = false))
        assertEquals("old-folder", db.drive().backup(task.id)?.folderId)
        assertFalse(binding.accepts("account", "new-folder"))
        db.drive().disconnect()
        assertEquals("reserved-id", db.drive().backup(task.id)?.remoteId)
        assertEquals("family.jpg", db.photos().find("original")?.displayName)
    }
}
