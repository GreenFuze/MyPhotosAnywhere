package com.myphotosanywhere.mpa.people

import com.google.android.gms.tasks.Task
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.tasks.await
import kotlinx.coroutines.withContext

/** One batch owns its clients from acquisition, including partial initialization and cancellation. */
internal class RecognitionResources : AutoCloseable {
    private val owned=ArrayDeque<AutoCloseable>()
    private var closed=false

    fun <T: AutoCloseable> acquire(factory: ()->T): T {
        check(!closed) { "Recognition resource owner is closed." }
        val resource=factory()
        try { owned.addFirst(resource) }
        catch(failure: Throwable) {
            try { resource.close() } catch(cleanup: Throwable) { if(cleanup!==failure) failure.addSuppressed(cleanup) }
            throw failure
        }
        return resource
    }

    override fun close() {
        if(closed) return
        closed=true
        var failure: Throwable?=null
        while(owned.isNotEmpty()) {
            try { owned.removeFirst().close() }
            catch(error: Throwable) {
                val previous=failure
                if(previous==null) failure=error else if(previous!==error) previous.addSuppressed(error)
            }
        }
        failure?.let { throw it }
    }
}

/** ML Kit has no cancellation token: retain its input until the native task really finishes. */
internal suspend fun <T> Task<T>.awaitOwnedInput(): T {
    val caller=currentCoroutineContext()
    caller.ensureActive()
    val result=try { withContext(NonCancellable) { await() } }
    catch(error: Throwable) { caller.ensureActive();throw error }
    caller.ensureActive()
    return result
}
