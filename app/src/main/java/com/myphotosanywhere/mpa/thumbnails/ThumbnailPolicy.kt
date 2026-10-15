package com.myphotosanywhere.mpa.thumbnails

import java.security.MessageDigest
import kotlin.math.ceil
import kotlin.math.sqrt

/** Physical-pixel crop demand, bounded by both the requested bucket and the phone heap. */
internal class ThumbnailPolicy(private val heapBytes: Long=Runtime.getRuntime().maxMemory()) {
    init { require(heapBytes>0) }
    private val buckets=listOf(256,384,512,768,1024,1536)
    val maximum=minOf(1536,sqrt(heapBytes/128.0).toInt().coerceAtLeast(256))
    fun dimension(width: Int,height: Int,aspect: Float): Int {
        require(width>0 && height>0 && aspect.isFinite() && aspect>0)
        val demand=ceil(maxOf(width.toDouble()/aspect,height.toDouble())*maxOf(aspect,1f)).coerceAtMost(maximum.toDouble()).toInt()
        return minOf(maximum,buckets.firstOrNull { it>=demand } ?: maximum)
    }
    fun key(identity: String,revision: String,dimension: Int): String {
        require(identity.isNotBlank() && dimension in 1..maximum)
        val raw="q2:${identity.length}:$identity:${revision.length}:$revision:$dimension"
        return MessageDigest.getInstance("SHA-256").digest(raw.toByteArray(Charsets.UTF_8)).joinToString("") { "%02x".format(it) }
    }
    fun diskBudget(thumbnailBudget: Long): Long {
        require(thumbnailBudget>=0)
        return minOf(64L*1024*1024,thumbnailBudget/4)
    }
    companion object {
        fun providerVersion(version: String?)=version?.let { "q2:$it" }
        fun currentProvider(cached: String?,version: String?)=version!=null && cached==providerVersion(version)
    }
}
