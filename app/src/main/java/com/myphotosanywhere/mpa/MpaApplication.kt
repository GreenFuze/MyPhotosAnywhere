package com.myphotosanywhere.mpa

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.data.PhotoRepository

class MpaApplication : Application() {
    val repository: PhotoRepository by lazy {
        PhotoRepository(this, Room.databaseBuilder(this, PhotoDatabase::class.java, "mpa.db").build())
    }
}
