package com.myphotosanywhere.mpa.data

/** The confirmation owns this exact media/source revision, never a mutable gallery position. */
internal data class DeviceCopyIdentity(val id: String,val sha: String,val bytes: Long,val binding: String,val remote: String,val version: String) {
    init { require(listOf(id,sha,binding,remote,version).all { it.isNotBlank() } && bytes>=0) }
    fun matches(photo: Photo)=photo.id==id && photo.sha256==sha && photo.bytes==bytes &&
        photo.sourceBindingKey==binding && photo.sourceRemoteId==remote && photo.cachedVersion==version
    companion object {
        fun from(photo: Photo): DeviceCopyIdentity? {
            val binding=photo.sourceBindingKey?.takeIf(String::isNotBlank) ?: return null
            val remote=photo.sourceRemoteId?.takeIf(String::isNotBlank) ?: return null
            val version=photo.cachedVersion?.takeIf(String::isNotBlank) ?: return null
            return DeviceCopyIdentity(photo.id,photo.sha256,photo.bytes,binding,remote,version)
        }
    }
}
