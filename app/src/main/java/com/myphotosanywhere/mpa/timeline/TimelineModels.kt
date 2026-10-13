package com.myphotosanywhere.mpa.timeline

import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto

/** Query identity is independent of the currently highlighted management account. */
internal data class TimelineQuery(val filter: String="All",val source: String?=null,
    val album: String?=null,val person: String?=null,val search: String?=null,
    val type: TimelineMediaType=TimelineMediaType.ALL,val fromMillis: Long?=null,val untilMillis: Long?=null,
    val searchZone: String=java.time.ZoneId.systemDefault().id,val includeMetadata: Boolean=false) {
    init {
        require(filter in setOf("All","Favorites","Offline","Videos","Edits","Archive"))
        require(search==null || search.length<=256)
        require(fromMillis==null || untilMillis==null || fromMillis<=untilMillis)
    }
    val importedPerson=com.myphotosanywhere.mpa.people.ImportedPersonLabel.fromKey(person)
    val needsMetadata get()=includeMetadata || searchText!=null || importedPerson!=null
    val searchText=search?.takeIf(String::isNotBlank)?.let { com.myphotosanywhere.mpa.search.SearchText(it) }
}

internal enum class TimelineMediaType { ALL, PHOTOS, VIDEOS }

internal enum class TimestampProvenance { CAPTURE, LINKED_CAPTURE, IMPORT, DRIVE_MODIFIED, UNKNOWN }

internal data class TimelineCursor(val millis: Long?,val key: String) {
    init { require(key.startsWith("photo:") || key.startsWith("cloud:")) }
}

/** A bounded read-only projection, never a second persistent media record. */
internal data class TimelineEntry(val key: String,val photoId: String?,val cloudKey: String?,
    val millis: Long?,val provenance: TimestampProvenance,val name: String,val mimeType: String,
    val originalPath: String,val thumbnailPath: String,val thumbnailRevision: Long,
    val bytes: Long,val sha256: String?,val md5: String?,val version: String?,
    val binding: String?,val remoteId: String?,val searchDocument: com.myphotosanywhere.mpa.search.SearchDocument?=null) {
    val cursor get()=TimelineCursor(millis,key)
    init {
        require((photoId!=null && key=="photo:$photoId") || (photoId==null && cloudKey!=null && key=="cloud:$cloudKey"))
        require((millis==null)==(provenance==TimestampProvenance.UNKNOWN))
    }
}

internal data class TimelineMedia(val entry: TimelineEntry,val photo: Photo?,val cloud: CloudPhoto?)

internal data class TimelineAnchor(val cursor: TimelineCursor,val offset: Int)

internal enum class TimelineDirection { AFTER, BEFORE }

/** Viewer order belongs to its originating snapshot, not the live grid's mutable indices. */
internal interface ViewerSession: AutoCloseable {
    val id: String
    val count: Int
    val initialPage: Int
    suspend fun entry(page: Int): TimelineEntry
}
