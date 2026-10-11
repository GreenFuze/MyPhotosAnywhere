package com.myphotosanywhere.mpa.importing

import android.app.Activity
import android.app.Application
import android.content.ClipData
import android.content.Intent
import android.net.Uri
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class TakeoutSelectionTest {
    private val context get() = RuntimeEnvironment.getApplication()

    @Test fun zipContractRequestsOpenableDocumentsAndLastingReadAccess() {
        val intent = TakeoutSelectionContract().createIntent(context, Unit)
        assertEquals(Intent.ACTION_OPEN_DOCUMENT, intent.action)
        assertTrue(intent.hasCategory(Intent.CATEGORY_OPENABLE))
        assertEquals("application/zip", intent.type)
        assertTrue(intent.getBooleanExtra(Intent.EXTRA_ALLOW_MULTIPLE, false))
        assertEquals(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION, intent.flags)
        assertEquals(0, intent.flags and Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
    }

    @Test fun treeContractAlsoReturnsPermissionFlags() {
        val contract = TakeoutSelectionContract(tree=true)
        assertEquals(Intent.ACTION_OPEN_DOCUMENT_TREE, contract.createIntent(context, Unit).action)
        val uri = Uri.parse("content://docs/tree/album")
        val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION
        val selection = requireNotNull(contract.parseResult(Activity.RESULT_OK, Intent().setData(uri).setFlags(flags)))
        assertEquals(listOf(uri), selection.uris)
        assertEquals(flags, selection.flags)
    }

    @Test fun multiPartResultDeduplicatesUrisWithoutLosingFlags() {
        val first = Uri.parse("content://docs/one"); val second = Uri.parse("content://docs/two")
        val intent = Intent().setData(first).setFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        intent.clipData = ClipData.newRawUri("ZIP", first).apply { addItem(ClipData.Item(second)) }
        val result = requireNotNull(TakeoutSelectionContract().parseResult(Activity.RESULT_OK, intent))
        assertEquals(listOf(first, second), result.uris)
        assertEquals(intent.flags, result.flags)
    }

    @Test fun cancelledOrEmptyResultDoesNotCreateAnImport() {
        val contract = TakeoutSelectionContract()
        assertNull(contract.parseResult(Activity.RESULT_CANCELED, Intent().setData(Uri.parse("content://docs/one"))))
        assertNull(contract.parseResult(Activity.RESULT_OK, null))
        assertNull(contract.parseResult(Activity.RESULT_OK, Intent()))
    }

    @Test fun transientOrWriteOnlyGrantFailsBeforeCreatingDurableWork() {
        val uri = Uri.parse("content://docs/one")
        for (flags in listOf(0, Intent.FLAG_GRANT_READ_URI_PERMISSION,
            Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)) {
            try { TakeoutAccess(context).retain(TakeoutSelection(listOf(uri),flags)); fail("Must require lasting read access") }
            catch (e: IllegalArgumentException) { assertTrue(requireNotNull(e.message).contains("Save the ZIP in Downloads")) }
        }
    }
}
