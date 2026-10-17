package com.myphotosanywhere.mpa

import androidx.room.Room
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveBackupWorker
import com.myphotosanywhere.mpa.importing.TakeoutCodec
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=MpaApplication::class)
class MpaApplicationLifecycleTest {
    @Test fun applicationDisposalStopsItsObserverClosesRoomAndPreservesOriginalsAndPeople()=runBlocking<Unit> {
        val app=RuntimeEnvironment.getApplication() as MpaApplication
        val file=File.createTempFile("lifecycle-original-",".jpg",app.cacheDir).apply { writeText("original bytes") }
        val metadata="""{"title":"photo.jpg","photoTakenTime":{"timestamp":"1"},"people":[{"name":"Synthetic lifecycle person"}]}"""
        val codec=TakeoutCodec()
        val photo=codec.apply(Photo("lifecycle-photo","photo.jpg",file.path,"","image/jpeg",file.length(),1,
            DriveBackupWorker.digest(file,"SHA-256"),capturedAt=1),codec.parse(metadata))
        try {
            val database=app.database
            database.photos().insert(photo);app.personLabels.drain()
            val person=database.personLabels().members(photo.id).single()
            assertTrue(database.isOpen)
            app.onTerminate();assertFalse(database.isOpen)
            app.onTerminate();assertFalse(database.isOpen)
            val restored=Room.databaseBuilder(app,PhotoDatabase::class.java,"mpa.db").build()
            try {
                assertEquals(photo,restored.photos().find(photo.id))
                assertEquals(person,restored.personLabels().members(photo.id).single())
            } finally { restored.close() }
            assertEquals("original bytes",file.readText())
            assertEquals(photo.sha256,DriveBackupWorker.digest(file,"SHA-256"))
        } finally { file.delete() }
    }
}
