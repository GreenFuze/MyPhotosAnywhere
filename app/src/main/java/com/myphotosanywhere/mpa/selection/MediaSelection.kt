package com.myphotosanywhere.mpa.selection

import android.os.Bundle
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.drive.CloudPhoto
import com.myphotosanywhere.mpa.timeline.TimelineEntry

/** Only stable identity and content verification survive selection; grid positions never do. */
internal data class SelectionTarget(val key: String,val name: String,val photoId: String?,val cloudKey: String?,
    val bytes: Long,val sha256: String?,val md5: String?,val version: String?) {
    init { require(key==photoId?.let { "photo:$it" } || photoId==null && key==cloudKey?.let { "cloud:$it" }) }
    fun matches(other: SelectionTarget)=key==other.key || (photoId==null || other.photoId==null) &&
        cloudKey!=null && cloudKey==other.cloudKey && bytes==other.bytes && when {
            !sha256.isNullOrBlank() && !other.sha256.isNullOrBlank() -> sha256==other.sha256
            md5!=null && other.md5!=null -> md5==other.md5
            else -> version==other.version
        }
    fun accepts(row: CloudPhoto)=!row.removed && row.bytes==bytes && when {
        !sha256.isNullOrBlank() && !row.sha256.isNullOrBlank() -> sha256==row.sha256
        md5!=null && row.md5!=null -> md5==row.md5
        else -> version==row.version
    }
    fun save()=Bundle().apply {
        putString("key",key);putString("name",name);putString("photo",photoId);putString("cloud",cloudKey)
        putLong("bytes",bytes);putString("sha",sha256);putString("md5",md5);putString("version",version)
    }
    companion object {
        fun restore(value: Bundle)=SelectionTarget(requireNotNull(value.getString("key")),requireNotNull(value.getString("name")),
            value.getString("photo"),value.getString("cloud"),value.getLong("bytes"),value.getString("sha"),value.getString("md5"),value.getString("version"))
        fun from(entry: TimelineEntry)=SelectionTarget(entry.key,entry.name,entry.photoId,entry.cloudKey,entry.bytes,entry.sha256,entry.md5,entry.version)
        fun from(photo: Photo,row: CloudPhoto?=null)=SelectionTarget("photo:${photo.id}",photo.displayName,photo.id,row?.key,
            photo.bytes,photo.sha256,row?.md5,photo.cachedVersion)
        fun from(row: CloudPhoto)=SelectionTarget("cloud:${row.key}",row.name,null,row.key,row.bytes,row.sha256,row.md5,row.version)
    }
}

internal class MediaSelection(initial: List<SelectionTarget> = emptyList()) {
    private val targets=initial.toMutableList()
    init { require(initial.size<=MAX_ITEMS && initial.map { it.key }.distinct().size==initial.size) }
    fun snapshot()=targets.toList()
    fun contains(target: SelectionTarget)=targets.any { it.matches(target) }
    /** False is a bounded selection limit, never a silently dropped photo. */
    fun toggle(target: SelectionTarget): Boolean {
        val old=targets.indexOfFirst { it.matches(target) }
        if(old>=0) targets.removeAt(old)
        else { if(targets.size==MAX_ITEMS) return false;targets.add(target) }
        return true
    }
    companion object { const val MAX_ITEMS=500 }
}

internal enum class BulkKind { FAVORITE, UNFAVORITE, ARCHIVE, RESTORE, ALBUM, REMOVE_ALBUM, OFFLINE, SHARE }
internal data class BulkAction(val kind: BulkKind,val albumId: String?=null) {
    init { require((kind in setOf(BulkKind.ALBUM,BulkKind.REMOVE_ALBUM))==(albumId!=null)) }
}
internal data class SelectionFailure(val target: SelectionTarget,val reason: String)
internal data class SelectionResult(val completed: Int,val failures: List<SelectionFailure>,val queued: Boolean=false,val cancelled: Boolean=false)
