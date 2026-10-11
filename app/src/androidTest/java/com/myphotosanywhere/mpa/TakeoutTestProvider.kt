package com.myphotosanywhere.mpa

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.os.Binder
import android.os.Bundle
import android.os.ParcelFileDescriptor
import java.io.File

/** Test APK owns these bytes; the app can read only through the explicitly granted content URI. */
class TakeoutTestProvider : ContentProvider() {
    private val archive get() = File(requireNotNull(context).cacheDir, "private-takeout.zip")
    override fun onCreate() = true
    override fun call(method: String, arg: String?, extras: Bundle?): Bundle {
        val ctx = requireNotNull(context)
        check(ctx.packageManager.getPackagesForUid(Binder.getCallingUid())?.contains("com.myphotosanywhere.mpa") == true)
        val uri = Uri.parse("content://com.myphotosanywhere.mpa.test.takeout/archive")
        if (method == "prepare") {
            java.util.zip.ZipOutputStream(archive.outputStream()).use { zip ->
                zip.putNextEntry(java.util.zip.ZipEntry("Album/photo.jpg"))
                zip.write(byteArrayOf(1,2,3,4)); zip.closeEntry()
                zip.putNextEntry(java.util.zip.ZipEntry("Album/photo.jpg.json"))
                zip.write("{\"title\":\"photo.jpg\",\"description\":\"Granted document\",\"photoTakenTime\":{\"timestamp\":\"123\"}}".toByteArray())
                zip.closeEntry()
            }
            val identity = Binder.clearCallingIdentity()
            try {
                ctx.grantUriPermission("com.myphotosanywhere.mpa", uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)
            } finally { Binder.restoreCallingIdentity(identity) }
            return Bundle().apply { putString("path", archive.absolutePath) }
        }
        check(method == "cleanup")
        val identity = Binder.clearCallingIdentity()
        try { ctx.revokeUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        finally { Binder.restoreCallingIdentity(identity) }
        archive.delete()
        return Bundle()
    }
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        require(mode == "r")
        return when (uri.lastPathSegment) {
            "archive" -> ParcelFileDescriptor.open(archive, ParcelFileDescriptor.MODE_READ_ONLY)
            "pipe" -> ParcelFileDescriptor.createPipe().let { pipe -> pipe[1].close(); pipe[0] }
            else -> throw SecurityException("Read access denied by test provider")
        }
    }
    override fun getType(uri: Uri) = "application/zip"
    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException()
}
