package com.myphotosanywhere.mpa.library

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto

/** Remote tombstones affect browsing, never the original file or its annotations. */
object GalleryVisibility {
    class Index(rows: List<CloudPhoto>,connected: Set<String>) {
        private val connectedRows=rows.filter { it.bindingKey in connected }
        private val byPhoto=connectedRows.filter { it.photoId!=null }.groupBy { it.photoId }
        private val byRemote=connectedRows.associateBy { it.bindingKey to it.remoteId }
        fun removedFromDrive(photo: Photo): Boolean {
            if(photo.sourceRemoteId==null) return false // Imported originals are independently owned.
            val links=byPhoto[photo.id].orEmpty()+listOfNotNull(byRemote[photo.sourceBindingKey to photo.sourceRemoteId])
            return links.isNotEmpty() && links.all { it.removed }
        }
        fun showLocal(photo: Photo,filter: String,collection: Boolean=false): Boolean =
            !removedFromDrive(photo) || filter in listOf("Offline","Favorites","Archive") || collection
    }
    fun removedFromDrive(photo: Photo,rows: List<CloudPhoto>,connected: Set<String>) = Index(rows,connected).removedFromDrive(photo)
    fun showLocal(photo: Photo,rows: List<CloudPhoto>,connected: Set<String>,filter: String,collection: Boolean=false) =
        Index(rows,connected).showLocal(photo,filter,collection)
}
