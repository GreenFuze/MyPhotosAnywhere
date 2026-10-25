package com.myphotosanywhere.mpa.processor.client

import android.database.sqlite.SQLiteDatabase
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.drive.DriveWorkspaceMetadata
import com.myphotosanywhere.mpa.drive.DriveWorkspacePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.LibraryConnectionDescriptor
import java.io.File
import java.io.IOException
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessorWorkspaceObservationDeviceTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private val pin = LibraryConnectionDescriptor(UUID.randomUUID(),UUID.randomUUID(),1,CatalogWorkspace("test-account","test-workspace"),key.public.encoded)
    private fun directory() = File(context.noBackupFilesDir,"workspace-test-${UUID.randomUUID()}")
    private fun store(directory: File) = ProcessorLibraryStateStore(context,StorageBudget(context),directory)
    private fun sql(directory: File,action: (SQLiteDatabase)->Unit) = SQLiteDatabase.openDatabase(File(directory,"connection.sqlite").absolutePath,null,SQLiteDatabase.OPEN_READWRITE).use(action)
    private suspend fun approve(store: ProcessorLibraryStateStore) {
        val connection = ProcessorLibraryConnection(store,object : ProcessorLibraryConnectionReader {
            override suspend fun account() = ProcessorLibraryAccount("test-account","synthetic@example.invalid")
            override suspend fun workspace(id: String) = ProcessorLibraryWorkspace(id,"Metadata",1,true,false)
        });connection.approve(connection.review(pin.encode()),pin.documentSha256)
    }
    private fun cleanup(directory: File) {
        check(directory.parentFile?.canonicalFile == context.noBackupFilesDir.canonicalFile && directory.name.startsWith("workspace-test-"))
        check(!directory.exists() || directory.deleteRecursively())
    }
    private fun metadata(id: String) = DriveWorkspaceMetadata(id,"application/json",1,2,listOf("test-workspace"),false,null,true)
    private fun source(ids: List<String>) = object : ProcessorWorkspaceInventorySource {
        override val connectionSha256 = pin.documentSha256
        override suspend fun cursor() = "cursor"
        override suspend fun page(token: String?) = DriveWorkspacePage(ids.map(::metadata),null,false)
        override suspend fun readCaptured(expected: DriveWorkspaceMetadata) = "{}".toByteArray()
    }

    @Test fun nativeFailureKeepsUnreadIdentityAcrossReopen() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);var observations = ProcessorWorkspaceObservations(store)
            try { ProcessorWorkspaceInventory(observations,source(listOf("a","b"))).prepare { _,_ -> throw IOException("Synthetic signature failure") };fail("Failed verification became successful") } catch (_: IOException) { }
            val previous = checkNotNull(observations.lastAttempt());assertTrue(previous.inventoryListed)
            sql(directory) { db -> db.rawQuery("SELECT count(*) FROM workspace_candidate",null).use { assertTrue(it.moveToFirst());assertEquals(2,it.getInt(0)) } }
            store.close();store = store(directory);observations = ProcessorWorkspaceObservations(store)
            assertEquals(previous,observations.lastAttempt())
            try { ProcessorWorkspaceInventory(observations,source(listOf("a"))).prepare { _,_ -> fail("Forgot unread metadata") };fail("Missing identity became success") } catch (_: IOException) { }
            assertTrue(checkNotNull(observations.lastAttempt()).revision > previous.revision)
            assertEquals(previous.firstUnresolved,observations.lastAttempt()?.firstUnresolved)
            try { observations.reads(previous);fail("Old owner remained current") } catch (_: ProcessorWorkspaceConflict) { }
        } finally { store.close();cleanup(directory) }
    }

    @Test fun nativeSchemaThreeKeepsOriginalAndApprovedAccount() = runBlocking<Unit> {
        val directory = directory();var store = store(directory)
        try {
            approve(store);val original = File(directory,"original.jpg").apply { writeBytes(byteArrayOf(7,5,3,1)) };store.close()
            sql(directory) { db -> db.execSQL("DROP TABLE workspace_event");db.execSQL("DROP TABLE workspace_read");db.execSQL("DROP TABLE workspace_candidate");db.execSQL("DROP TABLE workspace_attempt");db.version = 3 }
            store = store(directory);assertEquals(pin.encode(),store.current()?.encode());assertEquals("synthetic@example.invalid",store.registration()?.account?.email)
            val prepared = ProcessorWorkspaceInventory(ProcessorWorkspaceObservations(store),source(listOf("a"))).prepare { _,_ -> }
            assertEquals(1,prepared.reads.size);assertArrayEquals(byteArrayOf(7,5,3,1),original.readBytes())
            sql(directory) { assertEquals(4,it.version) }
        } finally { store.close();cleanup(directory) }
    }
}
