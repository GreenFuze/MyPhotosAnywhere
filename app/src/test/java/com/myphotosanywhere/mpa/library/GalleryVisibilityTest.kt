package com.myphotosanywhere.mpa.library

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto
import org.junit.Assert.*
import org.junit.Test

class GalleryVisibilityTest {
    private val photo=Photo("local","photo.jpg","/cached-original","/thumb","image/jpeg",8,1,"sha",
        sourceBindingKey="a",sourceRemoteId="remote",favorite=true)
    private val row=CloudPhoto("a:remote","a","remote","folder","photo.jpg","image/jpeg",8,"md5","1",
        null,null,null,null,photoId=photo.id)
    @Test fun deletionHidesDownloadedPhotoWithoutDeletingOfflineFavoritesOrAlbums() {
        val index=GalleryVisibility.Index(listOf(row.copy(removed=true)),setOf("a"))
        assertFalse(index.showLocal(photo,"All"));assertFalse(index.showLocal(photo,"Edits"))
        assertTrue(index.showLocal(photo,"Offline"));assertTrue(index.showLocal(photo,"Favorites"))
        assertTrue(index.showLocal(photo,"All",collection=true));assertTrue(index.showLocal(photo,"Archive"))
    }
    @Test fun activeOverlappingSourceKeepsTheSharedPhotoVisible() {
        val other=row.copy(key="b:remote",bindingKey="b")
        assertTrue(GalleryVisibility.Index(listOf(row.copy(removed=true),other),setOf("a","b")).showLocal(photo,"All"))
        assertFalse(GalleryVisibility.Index(listOf(row.copy(removed=true),other.copy(removed=true)),setOf("a","b")).showLocal(photo,"All"))
    }
    @Test fun disconnectedSourcesAndIndependentImportsRemainVisible() {
        assertTrue(GalleryVisibility.Index(listOf(row.copy(removed=true)),emptySet()).showLocal(photo,"All"))
        assertTrue(GalleryVisibility.Index(listOf(row.copy(removed=true)),setOf("a")).showLocal(photo.copy(sourceRemoteId=null),"All"))
    }
    @Test fun sourceTombstoneStillWorksWhenRemoteRevisionUnlinkedTheLocalId() {
        assertFalse(GalleryVisibility.Index(listOf(row.copy(removed=true,photoId=null)),setOf("a")).showLocal(photo,"All"))
    }
}
