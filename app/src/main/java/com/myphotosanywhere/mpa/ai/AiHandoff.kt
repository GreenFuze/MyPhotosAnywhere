package com.myphotosanywhere.mpa.ai

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import androidx.core.content.FileProvider
import com.myphotosanywhere.mpa.core.AiProvider
import com.myphotosanywhere.mpa.core.EditPrompt
import com.myphotosanywhere.mpa.data.Photo
import com.myphotosanywhere.mpa.data.PhotoRepository
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

object AiHandoff {
    suspend fun prepare(context: Context, photo: Photo, provider: AiProvider, instructions: String): Intent =
        withContext(Dispatchers.IO) {
            val prompt = EditPrompt.build(instructions)
            val dir = File(context.cacheDir, "handoff").apply { mkdirs() }
            // Old exports are disposable. Leave recent files available to receiving apps.
            val cutoff = System.currentTimeMillis() - 24 * 60 * 60 * 1000L
            dir.listFiles()?.filter { it.lastModified() < cutoff }?.forEach { it.delete() }
            val file = File(dir, "${UUID.randomUUID()}.jpg")
            val bitmap = PhotoRepository.preview(File(photo.originalPath), 2048)
            try {
                file.outputStream().use { check(bitmap.compress(Bitmap.CompressFormat.JPEG, 92, it)) }
            } catch (failure: Exception) {
                file.delete()
                throw failure
            } finally { bitmap.recycle() }
            // Re-encoding creates a new JPEG with no source EXIF or GPS metadata.
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.handoff", file)
            val send = Intent(Intent.ACTION_SEND).apply {
                type = "image/jpeg"
                putExtra(Intent.EXTRA_STREAM, uri)
                putExtra(Intent.EXTRA_TEXT, prompt)
                clipData = ClipData.newUri(context.contentResolver, "Selected photo", uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            withContext(Dispatchers.Main) {
                context.getSystemService(ClipboardManager::class.java)
                    .setPrimaryClip(ClipData.newPlainText("MPA editing instructions", prompt))
            }
            // A standard chooser avoids reliance on undocumented provider package names or deep links.
            Intent.createChooser(send, "Choose ${provider.title} to continue").apply {
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
        }
}
