package com.myphotosanywhere.mpa.search

import android.os.Bundle
import com.myphotosanywhere.mpa.timeline.TimelineMediaType
import com.myphotosanywhere.mpa.timeline.TimelineQuery
import java.time.LocalDate
import java.time.ZoneId

internal data class SearchDates(val first: LocalDate,val last: LocalDate) {
    init { require(first<=last) }
}
internal data class SearchCriteria(val text: String="",val type: TimelineMediaType=TimelineMediaType.ALL,
    val filter: String="All",val source: String?=null,val dates: SearchDates?=null) {
    init { require(text.length<=256);require(filter in setOf("All","Favorites","Offline","Edits","Archive")) }
    val filtered get()=type!=TimelineMediaType.ALL || filter!="All" || source!=null || dates!=null
    val browsing get()=text.isNotBlank() || filtered
    fun timeline(zone: ZoneId)=TimelineQuery(filter,source,search=text.trim(),type=type,
        fromMillis=dates?.first?.atStartOfDay(zone)?.toInstant()?.toEpochMilli(),
        untilMillis=dates?.last?.plusDays(1)?.atStartOfDay(zone)?.toInstant()?.toEpochMilli(),searchZone=zone.id)
    fun save()=Bundle().apply {
        putString("text",text);putString("type",type.name);putString("filter",filter);putString("source",source)
        dates?.let { putString("first",it.first.toString());putString("last",it.last.toString()) }
    }
    companion object {
        fun restore(bundle: Bundle)=SearchCriteria(bundle.getString("text").orEmpty(),
            TimelineMediaType.valueOf(requireNotNull(bundle.getString("type"))),requireNotNull(bundle.getString("filter")),
            bundle.getString("source"),bundle.getString("first")?.let { SearchDates(LocalDate.parse(it),LocalDate.parse(requireNotNull(bundle.getString("last")))) })
    }
}

/** Recents belong to saved screen state, never analytics or a durable search database. */
internal class RecentQueries(initial: List<String> = emptyList()) {
    init { require(initial.size<=LIMIT && initial.all { it.isNotBlank() && it.length<=256 } && initial.map { it.lowercase() }.distinct().size==initial.size) }
    private var values=initial.toList()
    fun snapshot()=values.toList()
    fun add(text: String) {
        val value=text.trim()
        require(value.length<=256)
        if(value.isNotEmpty()) values=(listOf(value)+values.filterNot { it.equals(value,true) }).take(LIMIT)
    }
    fun clear() { values=emptyList() }
    companion object { const val LIMIT=8 }
}
