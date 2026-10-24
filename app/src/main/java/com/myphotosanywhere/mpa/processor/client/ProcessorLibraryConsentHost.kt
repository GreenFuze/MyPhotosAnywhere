package com.myphotosanywhere.mpa.processor.client

import android.app.PendingIntent
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner

/** UI-thread owner of one foreground consent-launch attachment. Rotation/background
 * detaches only this host's exact identity; pending consent stays with the flow.
 * No Activity callback remains borrowed after stop, destroy or explicit close. */
internal class ProcessorLibraryConsentHost(private val lifecycle: Lifecycle, private val flow: ProcessorLibraryConnectionFlow,
    private val launch: (PendingIntent)->Unit) : DefaultLifecycleObserver, AutoCloseable {
    private var attachment: Any? = null
    private var closed = false
    init {
        try { lifecycle.addObserver(this) }
        catch (failure: Throwable) {
            try { close() } catch (cleanup: Throwable) { failure.addSuppressed(cleanup) }
            throw failure
        }
    }
    override fun onStart(owner: LifecycleOwner) {
        if (!closed && attachment == null) {
            val identity = flow.attachConsentLauncher(launch)
            // Transfer ownership before activation can invoke UI/lifecycle callbacks.
            attachment = identity
            try { flow.activateConsentLauncher(identity) }
            catch (failure: Throwable) { detach(); throw failure }
        }
    }
    override fun onStop(owner: LifecycleOwner) { detach() }
    override fun onDestroy(owner: LifecycleOwner) { close() }
    private fun detach() {
        val identity = attachment ?: return
        flow.detachConsentLauncher(identity); attachment = null
    }
    override fun close() {
        if (closed) return
        closed = true
        try { detach() } finally { lifecycle.removeObserver(this) }
    }
}
