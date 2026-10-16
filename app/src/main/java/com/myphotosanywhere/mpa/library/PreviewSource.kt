package com.myphotosanywhere.mpa.library

import androidx.core.net.toUri
import java.io.File

/** Content URIs are provider references, never relative filesystem paths. */
internal fun previewSource(path: String): Any?=when {
    path.isBlank() -> null
    path.startsWith("content://") -> path.toUri()
    else -> File(path)
}
