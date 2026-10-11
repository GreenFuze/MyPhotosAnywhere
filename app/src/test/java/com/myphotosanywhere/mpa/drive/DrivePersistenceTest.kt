package com.myphotosanywhere.mpa.drive

import android.app.Application
import android.net.Uri
import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import com.myphotosanywhere.mpa.data.LIBRARY_MIGRATION_2_3
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import java.io.File
import java.util.Base64
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
        .addMigrations(DRIVE_MIGRATION_1_2, LIBRARY_MIGRATION_2_3, SOURCES_MIGRATION_3_4, com.myphotosanywhere.mpa.data.METADATA_MIGRATION_4_5, com.myphotosanywhere.mpa.drive.SCAN_MIGRATION_5_6, com.myphotosanywhere.mpa.importing.IMPORT_MIGRATION_6_7).allowMainThreadQueries().build().also { database = it }

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

    @Test
    @Config(sdk = [26], application = Application::class)
    fun failedDriveLinkRollsBackThePhotoAndItsFilesTogether() = runBlocking {
        val db = open()
        val context = RuntimeEnvironment.getApplication()
        val source = File.createTempFile("mpa-fixture", ".png", context.cacheDir)
        // A generated 1x1 red PNG, rather than user photo data.
        source.writeBytes(Base64.getDecoder().decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC"))
        var newId: String? = null
        try {
            try {
                PhotoRepository(context, db).importPhoto(Uri.fromFile(source), nameOverride = "remote.png", mimeOverride = "image/png") { photo ->
                    newId = photo.id
                    db.drive().insertBackup(DriveBackup("link:${photo.id}", "binding", photo.id, "account", "owner@example.test",
                        "folder", DriveMode.APP_FILES.name, remoteId = "remote-image", state = "COMPLETE"))
                    error("Simulated failure while saving the remote link")
                }
                throw AssertionError("Expected the import transaction to fail")
            } catch (error: IllegalStateException) {
                assertEquals("Simulated failure while saving the remote link", error.message)
            }
            val id = requireNotNull(newId) // Ensures the real importer reached the transaction callback.
            assertEquals(listOf("original"), db.photos().all().map { it.id })
            assertEquals(null, db.drive().backup("link:$id"))
            assertFalse(File(context.filesDir, "originals/$id").exists())
            assertFalse(File(context.filesDir, "thumbnails/$id.jpg").exists())
        } finally { source.delete() }
    }
}
