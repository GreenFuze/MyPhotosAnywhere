package com.myphotosanywhere.mpa.people

import com.myphotosanywhere.mpa.data.Person
import com.myphotosanywhere.mpa.timeline.TimelineQuery
import com.myphotosanywhere.mpa.timeline.TimelineRepository
import com.myphotosanywhere.mpa.timeline.TimelineSql
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

internal data class CanonicalPersonGroup(val person: Person,val photos: Int,val thumbnail: String,val coverPhotoId: String?=null)
internal data class CanonicalPeopleState(val groups: List<CanonicalPersonGroup> = emptyList(),val loading: Boolean=true,val error: String?=null,val hasSnapshot: Boolean=false)

/** Count the same visible photo identities as the person grid, once per person. */
internal class CanonicalPeopleRepository(private val timeline: TimelineRepository) {
    fun changes()=callbackFlow {
        val observation=timeline.observe { trySend(Unit) };trySend(Unit)
        awaitClose { observation.close() }
    }.conflate()
    suspend fun groups(): List<CanonicalPersonGroup> = withContext(Dispatchers.IO) {
        timeline.database.openHelper.readableDatabase.query(TimelineSql(TimelineQuery()).peopleOverview()).use { rows ->
            buildList {
                while(rows.moveToNext()) {
                    currentCoroutineContext().ensureActive()
                    add(CanonicalPersonGroup(Person(rows.getString(0),rows.getString(1)),rows.getInt(2),rows.getString(3).orEmpty(),rows.getString(4)))
                }
            }
        }
    }
}
