package com.myphotosanywhere.mpa.importing

import java.nio.ByteBuffer
import java.nio.channels.ClosedChannelException
import java.nio.channels.NonWritableChannelException
import java.nio.channels.SeekableByteChannel

/** A single bounded range cache. Seeking never downloads the intervening archive bytes. */
class DriveRangeChannel(private val length: Long, private val budget: Long,
    private val fetch: (Long,Int)->ByteArray): SeekableByteChannel {
    private var opened=true
    private var cursor=0L
    private var fetched=0L
    private var cacheStart=-1L
    private var cache=ByteArray(0)
    init { require(length>0 && length<=1024L*1024*1024*1024 && budget>0) }
    private fun checkOpen() { if(!opened) throw ClosedChannelException() }
    override fun isOpen()=opened
    override fun close() { opened=false; cache=ByteArray(0) }
    override fun size(): Long { checkOpen(); return length }
    override fun position(): Long { checkOpen(); return cursor }
    override fun position(newPosition: Long): SeekableByteChannel {
        checkOpen(); require(newPosition>=0); cursor=newPosition; return this
    }
    override fun read(dst: ByteBuffer): Int {
        checkOpen()
        if(!dst.hasRemaining()) return 0
        if(cursor>=length) return -1
        if(cursor<cacheStart || cursor>=cacheStart+cache.size) {
            val start=cursor/BLOCK*BLOCK
            val count=minOf(BLOCK.toLong(),length-start).toInt()
            require(fetched+count<=budget) { "Drive ZIP read limit reached. Use smaller Takeout ZIP parts; source preserved." }
            val bytes=fetch(start,count)
            require(bytes.size==count) { "Incomplete Drive ZIP range. Resume with the unchanged archive." }
            cache=bytes; cacheStart=start; fetched+=count
        }
        val offset=(cursor-cacheStart).toInt()
        val count=minOf(dst.remaining(),cache.size-offset)
        dst.put(cache,offset,count); cursor+=count
        return count
    }
    override fun write(src: ByteBuffer): Int = throw NonWritableChannelException()
    override fun truncate(size: Long): SeekableByteChannel = throw NonWritableChannelException()
    companion object { const val BLOCK=256*1024 }
}
