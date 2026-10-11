package com.myphotosanywhere.mpa.importing

import java.io.File
import java.security.MessageDigest
import java.util.zip.CRC32
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

/** Hashes are accumulated together while bytes are already passing through bounded buffers. */
data class MediaFingerprint(val bytes: Long, val sha256: String, val md5: String, val crc: Long) {
    class Accumulator {
        private val sha=MessageDigest.getInstance("SHA-256")
        private val md5=MessageDigest.getInstance("MD5")
        private val crc=CRC32()
        private var bytes=0L
        fun update(buffer: ByteArray, count: Int) {
            bytes=Math.addExact(bytes,count.toLong())
            sha.update(buffer,0,count); md5.update(buffer,0,count); crc.update(buffer,0,count)
        }
        fun finish()=MediaFingerprint(bytes,hex(sha.digest()),hex(md5.digest()),crc.value)
        private fun hex(value: ByteArray)=value.joinToString("") { "%02x".format(it) }
    }
    companion object {
        suspend fun read(file: File, keepGoing: suspend () -> Unit = {}): MediaFingerprint {
            val hashes=Accumulator()
            val checkpoint=MediaReadCheckpoint(keepGoing)
            file.inputStream().use { input ->
                val buffer=ByteArray(65536)
                while(true) {
                    currentCoroutineContext().ensureActive(); checkpoint.check()
                    val count=input.read(buffer); if(count<0) break
                    hashes.update(buffer,count)
                }
            }
            checkpoint.check(true)
            return hashes.finish()
        }
    }
}

/** Cancellation remains checked each buffer; Room/state checks are bounded to four per second. */
class MediaReadCheckpoint(private val action: suspend () -> Unit,
    private val now: () -> Long = { System.nanoTime() }) {
    private var last: Long?=null
    suspend fun check(force: Boolean=false) {
        val time=now()
        if(force || last==null || time-requireNotNull(last)>=250_000_000L) {
            action(); last=time
        }
    }
}
