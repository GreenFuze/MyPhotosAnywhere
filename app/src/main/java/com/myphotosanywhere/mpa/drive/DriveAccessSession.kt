package com.myphotosanywhere.mpa.drive

import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** In-memory access only. A rejected old token must not invalidate a concurrent replacement. */
internal class DriveAccessSession(initial: String, private val refresh: (suspend (String) -> String)?) {
    private val lock = Mutex()
    private var access = initial.also { require(it.isNotBlank()) { "Drive access token missing." } }
    val canRefresh: Boolean get() = refresh != null

    suspend fun current(): String = lock.withLock { access }

    suspend fun rejected(token: String): String = lock.withLock {
        currentCoroutineContext().ensureActive()
        if (access != token) return@withLock access
        val replacement = requireNotNull(refresh) { "Drive token refresh is not configured." }(token)
        require(replacement.isNotBlank()) { "Drive returned no replacement access token." }
        currentCoroutineContext().ensureActive()
        access = replacement
        replacement
    }
}
