package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorLibraryConnectionDeviceTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun descriptor(): LibraryConnectionDescriptor {
        val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair().public.encoded
        return LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("synthetic-permission","synthetic-workspace"),key)
    }
    private class Reader : ProcessorLibraryConnectionReader {
        override suspend fun account() = ProcessorLibraryAccount("synthetic-permission","synthetic@example.invalid")
        override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Synthetic metadata",7,true,false)
    }
    private fun directory() = File(context.noBackupFilesDir,"processor-pin-test-${UUID.randomUUID()}")
    private fun cleanup(directory: File) {
        check(directory.parentFile?.canonicalFile == context.noBackupFilesDir.canonicalFile && directory.name.startsWith("processor-pin-test-"))
        check(!directory.exists() || directory.deleteRecursively())
    }

    @Test fun actualPrivateSqliteApprovalAndRecreationPreserveTheSamePinAndOriginalBytes() = runBlocking<Unit> {
        val directory = directory(); val descriptor = descriptor(); var pins = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
        try {
            val client = ProcessorLibraryConnection(pins,Reader()); val review = client.review(descriptor.encode())
            assertNull(pins.current()); val original = File(directory,"preserved-original.jpg").apply { writeBytes(byteArrayOf(4,3,2,1)) }
            client.approve(review,descriptor.documentSha256); pins.close(); pins = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
            val recreated = ProcessorLibraryConnection(pins,Reader())
            assertEquals(descriptor.encode(),pins.current()?.encode())
            assertEquals(descriptor.encode(),recreated.approve(recreated.review(descriptor.encode()),descriptor.documentSha256).encode())
            assertArrayEquals(byteArrayOf(4,3,2,1),original.readBytes())
        } finally { pins.close(); cleanup(directory) }
    }

    @Test fun canceledOwnerCleanupActuallyClosesAndMissingAcceptedNativePinFailsFast() = runBlocking<Unit> {
        val directory = directory(); val descriptor = descriptor(); var pins = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
        try {
            val client = ProcessorLibraryConnection(pins,Reader()); client.approve(client.review(descriptor.encode()),descriptor.documentSha256)
            supervisorScope {
                val entered = CompletableDeferred<Unit>()
                val owner = async { try { entered.complete(Unit); awaitCancellation() } finally { pins.close() } }
                try { withTimeout(5000) { entered.await() } }
                finally { owner.cancel(); try { owner.await(); fail("Canceled caller completed") } catch (_: CancellationException) { } }
            }
            try { pins.current(); fail("Canceled cleanup retained its SQLite owner") } catch (_: IllegalStateException) { }
            pins = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
            assertEquals(descriptor.encode(),pins.current()?.encode())
            SQLiteDatabase.openDatabase(File(directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use { it.execSQL("DELETE FROM approved_pin") }
            try { pins.current(); fail("Missing accepted root was treated as fresh setup") } catch (_: IllegalStateException) { }
        } finally { pins.close(); cleanup(directory) }
    }
}
