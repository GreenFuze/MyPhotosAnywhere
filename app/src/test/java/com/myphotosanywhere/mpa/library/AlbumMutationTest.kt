package com.myphotosanywhere.mpa.library

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class AlbumMutationTest {
    @Test fun staleCommandsChangeOnlyTheirColumnAndPreservePhotoMembershipAndDraft()=runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        try {
            val original=Album("album","Before",123)
            val photo=Photo("photo","photo.jpg","/original","/thumbnail","image/jpeg",8,1,"sha",pinned=true)
            val membership=AlbumPhoto(original.id,photo.id);val draft=EditDraft(photo.id,"draft",456)
            db.library().saveAlbum(original);db.photos().insert(photo);db.library().addToAlbum(membership);db.library().saveDraft(draft)
            assertEquals(1,db.library().pinAlbum(original.id,true))
            // A rename issued from the old unpinned menu snapshot cannot overwrite the pin.
            assertEquals(1,db.library().renameAlbum(original.id,"After"))
            assertEquals(original.copy(name="After",pinned=true),db.library().allAlbums().single())
            // An unpin issued from the old name snapshot cannot restore that old name.
            assertEquals(1,db.library().pinAlbum(original.id,false))
            assertEquals(original.copy(name="After"),db.library().allAlbums().single())
            assertEquals(listOf(membership),db.library().members(original.id))
            assertEquals(photo,db.photos().find(photo.id));assertEquals(draft,db.library().draft(photo.id))
        } finally { db.close() }
    }
    @Test fun staleRenameAndPinCannotRecreateDeletedAlbum()=runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        try {
            val album=Album("gone","Gone",1)
            db.library().saveAlbum(album);db.library().deleteAlbum(album.id)
            assertEquals(0,db.library().renameAlbum(album.id,"Resurrected"))
            assertEquals(0,db.library().pinAlbum(album.id,true))
            assertTrue(db.library().allAlbums().isEmpty())
        } finally { db.close() }
    }
}
