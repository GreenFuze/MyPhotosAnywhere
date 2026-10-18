package com.myphotosanywhere.mpa

import android.app.Application
import kotlinx.coroutines.*
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
    private val databaseOwner=lazy {
        Room.databaseBuilder(this, PhotoDatabase::class.java, "mpa.db")
            .addMigrations(DRIVE_MIGRATION_1_2, LIBRARY_MIGRATION_2_3, SOURCES_MIGRATION_3_4, com.myphotosanywhere.mpa.data.METADATA_MIGRATION_4_5, com.myphotosanywhere.mpa.drive.SCAN_MIGRATION_5_6, com.myphotosanywhere.mpa.importing.IMPORT_MIGRATION_6_7, com.myphotosanywhere.mpa.importing.TAKEOUT_DOWNLOAD_MIGRATION_7_8, com.myphotosanywhere.mpa.drive.HASH_INDEX_MIGRATION_8_9, com.myphotosanywhere.mpa.trash.TRASH_MIGRATION_9_10,com.myphotosanywhere.mpa.data.LIBRARY_FOLLOWUP_MIGRATION_10_11, com.myphotosanywhere.mpa.data.DEVICE_STORAGE_MIGRATION_11_12)
            .addCallback(com.myphotosanywhere.mpa.metadata.METADATA_CALLBACK)
            .addCallback(object: androidx.room.RoomDatabase.Callback() {
                override fun onCreate(db: androidx.sqlite.db.SupportSQLiteDatabase) { com.myphotosanywhere.mpa.data.installPersonLabelTriggers(db) }
                override fun onOpen(db: androidx.sqlite.db.SupportSQLiteDatabase) { com.myphotosanywhere.mpa.data.installPersonLabelTriggers(db) }
            }).build()
    }
    val database: PhotoDatabase by databaseOwner
    val repository: PhotoRepository by lazy {
        PhotoRepository(this, database)
    }
    internal val deviceMedia by lazy { com.myphotosanywhere.mpa.device.DeviceMediaLibrary(this,database) }
    private val personLabelsOwner=lazy { com.myphotosanywhere.mpa.people.CanonicalPeopleIndex(database) { if(settings.state.value.faceIndexing) people.refreshSuggestions() } }
    internal val personLabels by personLabelsOwner
    private val startupOwner=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    override fun onCreate() {
        super.onCreate()
        startupOwner.launch { personLabels.start() }
    }
    /** Emulated application disposal must release observers before closing their Room connection. */
    override fun onTerminate() {
        try {
            runBlocking {
                withTimeout(5000) {
                    startupOwner.coroutineContext.job.cancelAndJoin()
                    if(personLabelsOwner.isInitialized()) personLabels.close()
                }
            }
        } finally {
            try { if(databaseOwner.isInitialized()) database.close() }
            finally { super.onTerminate() }
        }
    }
    internal val galleryPresentation by lazy { com.myphotosanywhere.mpa.timeline.GalleryPresentation(this) }
    internal val timeline by lazy { com.myphotosanywhere.mpa.timeline.TimelineRepository(database,deviceReadable={ deviceMedia.readable(it) }) }
    internal val viewerSessions by lazy { com.myphotosanywhere.mpa.timeline.ViewerSessionStore(java.io.File(noBackupFilesDir,"viewer-sessions"),timeline,storage) }
    val settings by lazy { com.myphotosanywhere.mpa.core.LibrarySettings(this) }
    val storage by lazy { com.myphotosanywhere.mpa.data.StorageBudget(this) }
    val cache by lazy { com.myphotosanywhere.mpa.data.MediaCache(this, database, settings) }
    internal val thumbnailVariants by lazy { com.myphotosanywhere.mpa.thumbnails.AdaptiveThumbnailStore(java.io.File(cacheDir,"thumbnail-variants"),cache,storage,settings) }
    val downloads by lazy { com.myphotosanywhere.mpa.drive.CloudDownloads(this) }
    val sync by lazy { com.myphotosanywhere.mpa.drive.CloudSync(this, database) }
    val metadata by lazy { com.myphotosanywhere.mpa.metadata.MetadataRepository(this) }
    val people by lazy { com.myphotosanywhere.mpa.people.PeopleRepository(this, database, cache, settings) }
    val drive: DriveCoordinator by lazy { DriveCoordinator(this, database) }
    internal val trash by lazy { com.myphotosanywhere.mpa.trash.DriveTrashRepository(this) }
    val imports by lazy { com.myphotosanywhere.mpa.importing.ImportRepository(this) }
    val takeoutMetadata by lazy { com.myphotosanywhere.mpa.importing.DriveTakeoutMetadata(this) }
}
