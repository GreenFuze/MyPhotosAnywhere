package com.myphotosanywhere.mpa.importing

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.activity.result.contract.ActivityResultContract

data class TakeoutSelection(val uris: List<Uri>, val flags: Int)

/** Keep the result flags: a URI alone does not describe the permission Android granted. */
class TakeoutSelectionContract(private val tree: Boolean = false) : ActivityResultContract<Unit, TakeoutSelection?>() {
    override fun createIntent(context: Context, input: Unit): Intent =
        Intent(if (tree) Intent.ACTION_OPEN_DOCUMENT_TREE else Intent.ACTION_OPEN_DOCUMENT).apply {
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)
            if (!tree) {
                addCategory(Intent.CATEGORY_OPENABLE)
                type = "application/zip"
                putExtra(Intent.EXTRA_MIME_TYPES, arrayOf("application/zip", "application/x-zip-compressed"))
                putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
            }
        }

    override fun parseResult(resultCode: Int, intent: Intent?): TakeoutSelection? {
        if (resultCode != Activity.RESULT_OK || intent == null) return null
        val uris = buildList {
            intent.data?.let { add(it) }
            intent.clipData?.let { clip -> repeat(clip.itemCount) { add(clip.getItemAt(it).uri) } }
        }.distinct()
        return uris.takeIf { it.isNotEmpty() }?.let { TakeoutSelection(it, intent.flags) }
    }
}

class TakeoutAccess(private val context: Context) {
    fun retain(selection: TakeoutSelection) {
        val resolver = context.contentResolver
        for (uri in selection.uris) {
            require(uri.scheme == "content") { "Choose the ZIP using Android's Files picker." }
            if (resolver.persistedUriPermissions.any { it.uri == uri && it.isReadPermission }) continue
            require(selection.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0 &&
                selection.flags and Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION != 0) {
                "This file provider did not grant lasting read access. Save the ZIP in Downloads, then select it using Files."
            }
            try {
                resolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)
            } catch (e: SecurityException) {
                throw IllegalStateException("File access could not be retained. Save the ZIP in Downloads, then select it again using Files.", e)
            }
        }
    }
}
