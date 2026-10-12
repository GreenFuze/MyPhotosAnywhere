package com.myphotosanywhere.mpa.data

import android.content.Context
import android.os.StatFs
import android.os.storage.StorageManager
import java.io.File

/** Reserves filesystem capacity without deleting originals or altering cache ownership. */
class StorageBudget(private val capacity: Capacity) {
    constructor(context: Context): this(AndroidCapacity(context))

    interface Capacity {
        fun available(directory: File): Long
        fun allocatable(directory: File): Long
        fun allocate(directory: File,bytes: Long)
    }

    fun available(directory: File): Long =
        minOf(capacity.available(directory),capacity.allocatable(directory)).coerceAtLeast(0)

    fun requireBytes(directory: File,bytes: Long,message: String) {
        require(bytes>=0 && bytes<Long.MAX_VALUE)
        val required=bytes+1 // Existing import checks require strictly more than the reserve.
        if(capacity.available(directory)<required) {
            require(capacity.allocatable(directory)>=required) { message }
            capacity.allocate(directory,required)
        }
        require(available(directory)>=required) { message }
    }

    private class AndroidCapacity(context: Context): Capacity {
        private val manager=requireNotNull(context.getSystemService(StorageManager::class.java))
        override fun available(directory: File)=StatFs(directory.absolutePath).availableBytes
        override fun allocatable(directory: File)=manager.getAllocatableBytes(manager.getUuidForPath(directory))
        override fun allocate(directory: File,bytes: Long) {
            // Android can reclaim disposable cache; app files and source documents are protected.
            manager.allocateBytes(manager.getUuidForPath(directory),bytes)
        }
    }
}
