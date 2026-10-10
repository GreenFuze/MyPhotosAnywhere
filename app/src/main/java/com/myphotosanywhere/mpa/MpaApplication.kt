package com.myphotosanywhere.mpa

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.LIBRARY_MIGRATION_2_3
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import com.myphotosanywhere.mpa.drive.DRIVE_MIGRATION_1_2
import com.myphotosanywhere.mpa.drive.DriveCoordinator
import com.myphotosanywhere.mpa.drive.SOURCES_MIGRATION_3_4

class MpaApplication : Application() {
    val database: PhotoDatabase by lazy {
        Room.databaseBuilder(this, PhotoDatabase::class.java, "mpa.db")
            .addMigrations(DRIVE_MIGRATION_1_2, LIBRARY_MIGRATION_2_3, SOURCES_MIGRATION_3_4, com.myphotosanywhere.mpa.data.METADATA_MIGRATION_4_5)
            .addCallback(com.myphotosanywhere.mpa.metadata.METADATA_CALLBACK).build()
    }
    val repository: PhotoRepository by lazy {
        PhotoRepository(this, database)
    }
    val settings by lazy { com.myphotosanywhere.mpa.core.LibrarySettings(this) }
    val cache by lazy { com.myphotosanywhere.mpa.data.MediaCache(this, database, settings) }
    val downloads by lazy { com.myphotosanywhere.mpa.drive.CloudDownloads(this) }
    val sync by lazy { com.myphotosanywhere.mpa.drive.CloudSync(this, database) }
    val metadata by lazy { com.myphotosanywhere.mpa.metadata.MetadataRepository(this) }
    val people by lazy { com.myphotosanywhere.mpa.people.PeopleRepository(this, database, cache, settings) }
    val drive: DriveCoordinator by lazy { DriveCoordinator(this, database) }
}
