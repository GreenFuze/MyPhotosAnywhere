package com.myphotosanywhere.mpa

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.LIBRARY_MIGRATION_2_3
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import com.myphotosanywhere.mpa.drive.DRIVE_MIGRATION_1_2
import com.myphotosanywhere.mpa.drive.DriveCoordinator
import com.myphotosanywhere.mpa.drive.SOURCES_MIGRATION_3_4

class MpaApplication : Application(), androidx.work.Configuration.Provider {
    override val workManagerConfiguration: androidx.work.Configuration
        get() = androidx.work.Configuration.Builder().setJobSchedulerJobIdRange(1,999_999).build()
    val database: PhotoDatabase by lazy {
        Room.databaseBuilder(this, PhotoDatabase::class.java, "mpa.db")
            .addMigrations(DRIVE_MIGRATION_1_2, LIBRARY_MIGRATION_2_3, SOURCES_MIGRATION_3_4, com.myphotosanywhere.mpa.data.METADATA_MIGRATION_4_5, com.myphotosanywhere.mpa.drive.SCAN_MIGRATION_5_6, com.myphotosanywhere.mpa.importing.IMPORT_MIGRATION_6_7, com.myphotosanywhere.mpa.importing.TAKEOUT_DOWNLOAD_MIGRATION_7_8, com.myphotosanywhere.mpa.drive.HASH_INDEX_MIGRATION_8_9, com.myphotosanywhere.mpa.trash.TRASH_MIGRATION_9_10)
            .addCallback(com.myphotosanywhere.mpa.metadata.METADATA_CALLBACK).build()
    }
    val repository: PhotoRepository by lazy {
        PhotoRepository(this, database)
    }
    internal val galleryPresentation by lazy { com.myphotosanywhere.mpa.timeline.GalleryPresentation(this) }
    internal val timeline by lazy { com.myphotosanywhere.mpa.timeline.TimelineRepository(database) }
    internal val viewerSessions by lazy { com.myphotosanywhere.mpa.timeline.ViewerSessionStore(java.io.File(noBackupFilesDir,"viewer-sessions"),timeline,storage) }
    val settings by lazy { com.myphotosanywhere.mpa.core.LibrarySettings(this) }
    val storage by lazy { com.myphotosanywhere.mpa.data.StorageBudget(this) }
    val cache by lazy { com.myphotosanywhere.mpa.data.MediaCache(this, database, settings) }
    val downloads by lazy { com.myphotosanywhere.mpa.drive.CloudDownloads(this) }
    val sync by lazy { com.myphotosanywhere.mpa.drive.CloudSync(this, database) }
    val metadata by lazy { com.myphotosanywhere.mpa.metadata.MetadataRepository(this) }
    val people by lazy { com.myphotosanywhere.mpa.people.PeopleRepository(this, database, cache, settings) }
    val drive: DriveCoordinator by lazy { DriveCoordinator(this, database) }
    internal val trash by lazy { com.myphotosanywhere.mpa.trash.DriveTrashRepository(this) }
    val imports by lazy { com.myphotosanywhere.mpa.importing.ImportRepository(this) }
    val takeoutMetadata by lazy { com.myphotosanywhere.mpa.importing.DriveTakeoutMetadata(this) }
}
