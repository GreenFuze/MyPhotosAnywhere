package com.myphotosanywhere.mpa.people

import com.google.gson.JsonArray
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.importing.TakeoutCodec
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.withContext
import java.util.Base64

/** A provider label is not a reviewed face identity. Equal names from different sources stay separate. */
internal data class ImportedPersonLabel(val name: String,val binding: String?) {
    init { require(name.isNotBlank() && name==name.trim() && name.length<=200) }
    val key: String get() {
        val fields=JsonArray().apply { add(name);add(binding) }
        return PREFIX+Base64.getUrlEncoder().withoutPadding().encodeToString(fields.toString().toByteArray(Charsets.UTF_8))
    }
    companion object {
        private const val PREFIX="takeout-person:"
        fun fromKey(key: String?): ImportedPersonLabel? {
            if(key==null || !key.startsWith(PREFIX)) return null
            require(key.length<=4096)
            val fields=JsonParser.parseString(String(Base64.getUrlDecoder().decode(key.removePrefix(PREFIX)),Charsets.UTF_8)).asJsonArray
            require(fields.size()==2)
            return ImportedPersonLabel(fields[0].asString,if(fields[1].isJsonNull) null else fields[1].asString).also { require(it.key==key) }
        }
    }
}

internal class ImportedPeopleCodec {
    private val codec=TakeoutCodec()
    fun names(importedMetadata: String): List<String> {
        if(importedMetadata.isBlank() || !importedMetadata.trimStart().startsWith("{")) return emptyList()
        val record=JsonParser.parseString(importedMetadata).asJsonObject.get("googleTakeout") ?: return emptyList()
        return codec.parse(record.toString()).people.map(String::trim).filter(String::isNotBlank).distinct()
    }
    fun matches(label: ImportedPersonLabel,binding: String?,metadata: String)=label.binding==binding && label.name in names(metadata)
}

internal data class ImportedPersonGroup(val label: ImportedPersonLabel,val photos: Int,val thumbnail: String)
internal data class ImportedPeopleState(val groups: List<ImportedPersonGroup> = emptyList(),val loading: Boolean=true,val error: String?=null)

/** Read retained sidecars through the normal visibility projection; do not manufacture faces or mutate metadata. */
internal class ImportedPeopleRepository(private val timeline: TimelineRepository) {
    private val codec=ImportedPeopleCodec()
    fun changes()=callbackFlow {
        val observation=timeline.observe { trySend(Unit) };trySend(Unit)
        awaitClose { observation.close() }
    }.conflate()
    suspend fun groups(): List<ImportedPersonGroup> = withContext(Dispatchers.IO) {
        val groups=linkedMapOf<String,ImportedPersonGroup>()
        timeline.database.openHelper.readableDatabase.query(TimelineSql(TimelineQuery(includeMetadata=true)).ordered()).use { rows ->
            timeline.forwardWindow(rows)
            while(rows.moveToNext()) {
                currentCoroutineContext().ensureActive()
                val entry=timeline.read(rows)
                if(entry.photoId==null) continue
                for(name in codec.names(requireNotNull(entry.searchDocument).metadata)) {
                    val label=ImportedPersonLabel(name,entry.binding)
                    val previous=groups[label.key]
                    groups[label.key]=ImportedPersonGroup(label,(previous?.photos ?: 0)+1,previous?.thumbnail?.takeIf(String::isNotBlank) ?: entry.thumbnailPath)
                }
            }
        }
        groups.values.sortedWith(compareBy<ImportedPersonGroup> { it.label.name.lowercase() }.thenBy { it.label.key })
    }
}
