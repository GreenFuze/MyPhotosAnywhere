package com.myphotosanywhere.mpa.people

import androidx.room.InvalidationTracker
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.util.UUID

/** Durable queue + bounded transactions: label indexing never requires biometric consent. */
internal class CanonicalPeopleIndex(private val database: PhotoDatabase,private val afterBatch: suspend ()->Unit={}) {
    private val owner=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private var task: Job?=null
    private val codec=ImportedPeopleCodec()
    private val draining=Mutex()
    private val active=MutableStateFlow(false)
    val indexing=active.asStateFlow()
    private val failure=MutableStateFlow<String?>(null)
    val error=failure.asStateFlow()
    @Synchronized fun start() {
        if(task?.isActive==true) return
        task=owner.launch {
            try {
                changes().collect {
                    if(drain()>0) afterBatch()
                    failure.value=if(database.personLabels().errors()>0) "Some photo labels could not be read. Other people are available. Retry failed labels." else null
                }
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: android.database.sqlite.SQLiteException) { failure.value=error.message ?: "Cannot update People. Saved names are preserved. Retry." }
            catch(error: IOException) { failure.value=error.message ?: "Cannot update People. Saved names are preserved. Retry." }
        }
    }
    fun retry() { owner.launch { database.personLabels().retry();start() } }
    internal suspend fun close() { owner.coroutineContext.job.cancelAndJoin() }
    private fun changes()=callbackFlow {
        val observer=object: InvalidationTracker.Observer("photos","people_index_queue") {
            override fun onInvalidated(tables: Set<String>) { trySend(Unit) }
        }
        database.invalidationTracker.addObserver(observer);trySend(Unit)
        awaitClose { database.invalidationTracker.removeObserver(observer) }
    }.conflate()
    suspend fun drain(): Int=withContext(Dispatchers.IO) { draining.withLock {
        active.value=true
        try {
        var completed=0
        while(true) {
            currentCoroutineContext().ensureActive()
            val batch=database.personLabels().pending(50)
            if(batch.isEmpty()) break
            for(id in batch) {
                currentCoroutineContext().ensureActive()
                try { index(id);completed++ }
                catch(error: IOException) { database.personLabels().failed(id,error.message ?: "Cannot read the retained people labels. Retry.") }
            }
            yield()
        }
        completed
        } finally { active.value=false }
    } }
    internal suspend fun index(id: String)=database.withTransaction {
        val labels=database.personLabels();val photo=database.photos().find(id)
        val previous=labels.members(id).associateBy { it.aliasKey }
        val expected=ArrayList<PhotoPersonLabel>()
        val names=if(photo==null) emptyList() else try { codec.names(photo.importedMetadata) }
            catch(error: com.google.gson.JsonParseException) { throw IOException("This photo has invalid retained people metadata. Its original is preserved.",error) }
            catch(error: IllegalArgumentException) { throw IOException("This photo’s retained people metadata is unsupported. Its original is preserved.",error) }
            catch(error: IllegalStateException) { throw IOException("This photo’s retained people metadata has an unexpected format. Its original is preserved.",error) }
        val origin=if(photo!=null && names.isNotEmpty()) labels.origin(id) ?: PhotoPeopleOrigin(id,photo.sourceBindingKey).also { labels.saveOrigin(it) } else null
        if(photo!=null) for(name in names) {
            val provider=ImportedPersonLabel(name,checkNotNull(origin).binding)
            var alias=labels.alias(provider.key)
            if(alias==null) {
                // Equal display names alone cannot prove that two source labels are one person.
                // Once explicitly merged, this alias reuses the chosen identity on every import.
                val person=Person(UUID.randomUUID().toString(),name)
                database.library().savePerson(person)
                alias=PersonAlias(provider.key,name,provider.binding,person.id)
                labels.saveAlias(alias)
            }
            if(!alias.ignored) {
                val personId=checkNotNull(alias.personId) { "Active provider alias has no person identity." }
                check(labels.person(personId)!=null) { "Provider alias references a missing person." }
                expected+=PhotoPersonLabel(id,alias.key,personId,previous[alias.key]?.excluded ?: false)
            }
        }
        expected+=previous.values.filter { it.excluded && expected.none { item -> item.aliasKey==it.aliasKey } }
        if(previous.values.toSet()!=expected.toSet()) {
            labels.clearPhoto(id);expected.forEach { labels.saveMember(it) }
        }
        labels.indexed(id)
    }
}
