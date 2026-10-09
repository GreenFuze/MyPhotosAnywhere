package com.myphotosanywhere.mpa

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository
import com.myphotosanywhere.mpa.drive.DRIVE_MIGRATION_1_2
import com.myphotosanywhere.mpa.drive.DriveCoordinator

class MpaApplication : Application() {
    val database: PhotoDatabase by lazy {
        Room.databaseBuilder(this, PhotoDatabase::class.java, "mpa.db")
            .addMigrations(DRIVE_MIGRATION_1_2).build()
    }
    val repository: PhotoRepository by lazy {
        PhotoRepository(this, database)
    }
    val drive: DriveCoordinator by lazy { DriveCoordinator(this, database) }
}
