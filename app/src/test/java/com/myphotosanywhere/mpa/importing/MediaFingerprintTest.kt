package com.myphotosanywhere.mpa.importing

import android.app.Application
import androidx.room.Room
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class MediaFingerprintTest {
    private val storage=com.myphotosanywhere.mpa.data.StorageBudget(object: com.myphotosanywhere.mpa.data.StorageBudget.Capacity {
        override fun available(directory: java.io.File)=Long.MAX_VALUE
        override fun allocatable(directory: java.io.File)=Long.MAX_VALUE
        override fun allocate(directory: java.io.File,bytes: Long)=error("Unexpected allocation")
    })
    @Test fun streamedStagingComputesAllHashesFromTheBytesWritten() = runBlocking<Unit> {
        val bytes=ByteArray(180_000) { (it%251).toByte() }
        val file=java.io.File(RuntimeEnvironment.getApplication().cacheDir,"stream-fingerprint")
        try {
            var checks=0
            val hashes=TakeoutDocuments.copyBounded(bytes.inputStream(),file,storage) { checks++ }
            assertArrayEquals(bytes,file.readBytes())
            assertEquals(bytes.size.toLong(),hashes.bytes)
            assertEquals(com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"SHA-256"),hashes.sha256)
            assertEquals(com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(file,"MD5"),hashes.md5)
            assertEquals(java.util.zip.CRC32().apply { update(bytes) }.value,hashes.crc)
            assertEquals(hashes,MediaFingerprint.read(file))
            assertTrue(checks>=2)
        } finally { file.delete() }
    }
    @Test fun expensiveChecksAreThrottledButFinalPauseIsObserved() = runBlocking<Unit> {
        var time=0L; var calls=0
        val checkpoint=MediaReadCheckpoint({ calls++ },{ time })
        repeat(100) { checkpoint.check() }; assertEquals(1,calls)
        time=249_999_999; checkpoint.check(); assertEquals(1,calls)
        time++; checkpoint.check(); assertEquals(2,calls)
        checkpoint.check(true); assertEquals(3,calls)
        val file=java.io.File(RuntimeEnvironment.getApplication().cacheDir,"paused-fingerprint")
        try {
            try { TakeoutDocuments.copyBounded(byteArrayOf(1,2,3).inputStream(),file,storage) { error("paused") }; fail() }
            catch(e: IllegalStateException) { assertEquals("paused",e.message) }
            assertEquals(0,file.length())
        } finally { file.delete() }
    }
}
