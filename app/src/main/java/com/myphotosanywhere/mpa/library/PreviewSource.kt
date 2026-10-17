package com.myphotosanywhere.mpa.library

import java.io.File

/** Provider references require DeviceMediaLibrary validation before deriving owned preview bytes. */
internal fun previewSource(path: String): Any?=when {
    path.isBlank() -> null
    path.startsWith("content://") -> null
    else -> File(path)
}

/** Resolve only the requested canonical photo, never another row currently reusing its URI. */
@androidx.compose.runtime.Composable
internal fun rememberPreview(path: String,photoId: String?): Any? {
    val app=androidx.compose.ui.platform.LocalContext.current.applicationContext as com.myphotosanywhere.mpa.MpaApplication
    return androidx.compose.runtime.produceState(previewSource(path),path,photoId) {
        value=previewSource(path)
        if(path.startsWith("content://") && photoId!=null) try {
            val photo=kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { app.database.photos().find(photoId) }
            value=photo?.takeIf { it.thumbnailPath==path }?.let { app.cache.thumbnail(it) }
        } catch(cancelled: kotlinx.coroutines.CancellationException) { throw cancelled }
        catch(_: java.io.IOException) { value=null }
        catch(_: android.database.sqlite.SQLiteException) { value=null }
    }.value
}
