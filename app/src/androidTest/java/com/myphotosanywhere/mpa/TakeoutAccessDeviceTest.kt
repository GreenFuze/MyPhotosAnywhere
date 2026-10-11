package com.myphotosanywhere.mpa

import android.content.Intent
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.importing.TakeoutAccess
import com.myphotosanywhere.mpa.importing.TakeoutDocuments
import com.myphotosanywhere.mpa.importing.TakeoutSelection
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class TakeoutAccessDeviceTest {
    private val app get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val root = Uri.parse("content://com.myphotosanywhere.mpa.test.takeout")

    @Test fun grantedPrivateDocumentRetainsReadAccessAndStagesWithoutReopeningAPath() = runBlocking<Unit> {
        val resolver = app.contentResolver
        val uri = Uri.withAppendedPath(root, "archive")
        val staged = File(app.cacheDir, "granted-document-stage.jpg")
        val fixture = requireNotNull(resolver.call(root,"prepare",null,null))
        try {
            // Reopening a provider-owned pathname is forbidden even though Android granted the descriptor.
            try { File(requireNotNull(fixture.getString("path"))).inputStream().close(); fail("Private provider file must not be path-readable") }
            catch (_: java.io.FileNotFoundException) { }
            TakeoutAccess(app).retain(TakeoutSelection(listOf(uri),
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION))
            assertTrue(resolver.persistedUriPermissions.any { it.uri == uri && it.isReadPermission })
            // Recreate the reader, as a resumed worker does after the picker callback has returned.
            val items = TakeoutDocuments(app).manifest("provider-job",uri,false)
            val media = items.single { it.mime == "image/jpeg" }
            assertTrue(requireNotNull(media.metadata).contains("Granted document"))
            assertEquals("MATCHED", items.single { it.mime == "application/json" }.state)
            TakeoutDocuments(app).stage(media,staged,{})
            assertArrayEquals(byteArrayOf(1,2,3,4), staged.readBytes())
            assertEquals(2,TakeoutDocuments(app).manifest("provider-job",uri,false).size)
        } finally {
            staged.delete()
            if (resolver.persistedUriPermissions.any { it.uri == uri })
                resolver.releasePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION)
            resolver.call(root,"cleanup",null,null)
        }
    }

    @Test fun nonSeekableProviderFailsWithLocalFileRecoveryAdvice() = runBlocking<Unit> {
        try { TakeoutDocuments(app).manifest("pipe-job",Uri.withAppendedPath(root,"pipe"),false); fail() }
        catch (e: IllegalStateException) { assertTrue(requireNotNull(e.message).contains("Save it in Downloads")) }
    }

    @Test fun revokedProviderAccessShowsReselectionAdvice() = runBlocking<Unit> {
        try { TakeoutDocuments(app).manifest("denied-job",Uri.withAppendedPath(root,"denied"),false); fail() }
        catch (e: IllegalStateException) { assertTrue(requireNotNull(e.message).contains("Select it again using Files")) }
    }

    @Test fun providerRefusingPersistenceShowsRecoveryInsteadOfRawAccessDenied() {
        try {
            TakeoutAccess(app).retain(TakeoutSelection(listOf(Uri.withAppendedPath(root,"ungranted")),
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION))
            fail("Provider never granted this document")
        } catch (e: IllegalStateException) {
            assertTrue(requireNotNull(e.message).contains("select it again using Files"))
        }
    }
}
