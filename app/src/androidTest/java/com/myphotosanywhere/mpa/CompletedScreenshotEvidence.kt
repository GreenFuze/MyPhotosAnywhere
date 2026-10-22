package com.myphotosanywhere.mpa

import android.app.Instrumentation
import android.content.ContentValues
import android.graphics.Bitmap
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import java.io.ByteArrayOutputStream

/** One captured frame, fully encoded and verified before publishing its evidence file.
 * Never recaptures another UI state or retries a test assertion. Failed files remain
 * available to the artifact collector; they are never presented as completed evidence. */
internal class CompletedScreenshotEvidence(private val instrumentation: Instrumentation) {
    fun capture(name: String) {
        require(name.matches(Regex("[A-Za-z0-9_-]+")))
        val image = requireNotNull(instrumentation.uiAutomation.takeScreenshot())
        val bytes = try {
            check(image.width in 1..8192 && image.height in 1..8192 && image.width.toLong() * image.height * 4 <= MAXIMUM_BYTES)
            BoundedPngOutput().use { output ->
                check(image.compress(Bitmap.CompressFormat.PNG, 100, output)) { "Screenshot PNG encoding failed." }
                output.toByteArray().also { encoded ->
                    check(encoded.size >= 32 && encoded.copyOfRange(0, 8).contentEquals(PNG_HEADER) &&
                        encoded.copyOfRange(encoded.size - 12, encoded.size).contentEquals(PNG_END)) { "Screenshot encoder returned an incomplete PNG." }
                }
            }
        } finally { image.recycle() }
        val resolver = instrumentation.targetContext.contentResolver
        val uri = requireNotNull(resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, "$name.png")
            put(MediaStore.MediaColumns.MIME_TYPE, "image/png")
            put(MediaStore.MediaColumns.RELATIVE_PATH, "Download/MPA")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }))
        val descriptor = requireNotNull(resolver.openFileDescriptor(uri, "wt"))
        var transferred = false
        try {
            val output = ParcelFileDescriptor.AutoCloseOutputStream(descriptor)
            transferred = true // The stream now owns and closes this descriptor.
            output.use { it.write(bytes); it.flush(); it.fd.sync() }
        } finally { if (!transferred) descriptor.close() }
        requireNotNull(resolver.openInputStream(uri)).use { input ->
            val buffer = ByteArray(32 * 1024); var position = 0
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                check(count > 0 && position.toLong() + count <= bytes.size) { "Saved screenshot length differs from its captured frame." }
                for (offset in 0 until count) check(buffer[offset] == bytes[position + offset]) { "Saved screenshot differs from its captured frame." }
                position += count
            }
            check(position == bytes.size) { "Saved screenshot is truncated." }
        }
        check(resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) == 1) {
            "Screenshot evidence was not published."
        }
    }
    private class BoundedPngOutput : ByteArrayOutputStream() {
        override fun write(value: Int) { check(count < MAXIMUM_BYTES); super.write(value) }
        override fun write(bytes: ByteArray, offset: Int, length: Int) {
            check(length >= 0 && length.toLong() + count <= MAXIMUM_BYTES); super.write(bytes, offset, length)
        }
    }
    private companion object {
        const val MAXIMUM_BYTES = 64L * 1024 * 1024
        val PNG_HEADER = byteArrayOf(137.toByte(), 80, 78, 71, 13, 10, 26, 10)
        val PNG_END = byteArrayOf(0, 0, 0, 0, 73, 69, 78, 68, 174.toByte(), 66, 96, 130.toByte())
    }
}
