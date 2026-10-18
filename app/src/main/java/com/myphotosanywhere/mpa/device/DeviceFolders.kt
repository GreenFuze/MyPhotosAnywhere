package com.myphotosanywhere.mpa.device

/** Volume and relative directory identify a collection; filenames never identify media. */
internal data class DeviceFolderKey(val volume: String,val path: String) {
    init { require(volume.isNotBlank());require(!path.startsWith('/')) }
    val title get()=path.trimEnd('/').substringAfterLast('/').takeUnless { it.isBlank() || it.startsWith("bucket:") } ?: "Device photos"
    val key get()=com.google.gson.JsonArray().apply { add(volume);add(path) }.toString()
    companion object {
        fun restore(value: String): DeviceFolderKey {
            val parts=com.google.gson.JsonParser.parseString(value).asJsonArray
            require(parts.size()==2)
            return DeviceFolderKey(parts[0].asString,parts[1].asString)
        }
    }
}

internal data class DeviceFolderSummary(val volume: String,val folderPath: String,val name: String,val count: Long) {
    val identity get()=DeviceFolderKey(volume,folderPath)
    val title get()=name.ifBlank { identity.title }
}
