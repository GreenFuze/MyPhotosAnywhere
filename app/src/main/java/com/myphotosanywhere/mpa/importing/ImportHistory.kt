package com.myphotosanywhere.mpa.importing

import android.content.Context
import android.content.SharedPreferences
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.withContext

/** Presentation preference only: task rows, media, source grants and checkpoints stay intact. */
class ImportHistory(private val context: Context) {
    private val preferences=context.getSharedPreferences("import-history",Context.MODE_PRIVATE)
    private fun dismissed()=preferences.all.filter { it.key.startsWith("job:") && it.value==true }.keys.map { it.removePrefix("job:") }.toSet()
    fun observeDismissed()=callbackFlow {
        val listener=SharedPreferences.OnSharedPreferenceChangeListener { _,_ -> trySend(dismissed()) }
        preferences.registerOnSharedPreferenceChangeListener(listener)
        trySend(dismissed())
        awaitClose { preferences.unregisterOnSharedPreferenceChangeListener(listener) }
    }.distinctUntilChanged()
    suspend fun dismiss(database: PhotoDatabase,id: String): Boolean = database.withTransaction {
        val job=database.imports().job(id) ?: return@withTransaction false
        if(!canDismiss(job.state)) return@withTransaction false
        withContext(Dispatchers.IO) { synchronized(lock) {
            com.myphotosanywhere.mpa.core.CheckedPreferences(preferences).commit("Cannot save import dismissal.") { putBoolean("job:$id",true) }
            ImportNotifications(context).clearSummary(id)
        } }
        true
    }
    suspend fun restore(id: String) = withContext(Dispatchers.IO) { synchronized(lock) {
        com.myphotosanywhere.mpa.core.CheckedPreferences(preferences).commit("Cannot restore import history.") { remove("job:$id") }
    } }
    /** Share a lock with dismissal so a late worker cannot repost a dismissed summary. */
    fun withSummary(job: String,state: String,post: ()->Unit) = synchronized(lock) {
        if(!canDismiss(state) || !preferences.getBoolean("job:$job",false)) post()
    }
    companion object {
        private val lock=Any()
        fun canDismiss(state: String)=state in listOf("COMPLETE","CANCELLED","FAILED")
    }
}
