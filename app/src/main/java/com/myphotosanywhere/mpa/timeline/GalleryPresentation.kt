package com.myphotosanywhere.mpa.timeline

import android.content.Context
import com.myphotosanywhere.mpa.core.CheckedPreferences
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId
import java.time.Instant
import kotlin.math.roundToLong

internal enum class GridDensity(val columns: Int,val title: String) {
    LARGE(2,"Large"), DEFAULT(3,"Default"), COMPACT(4,"Compact");
    fun pinch(scale: Float): GridDensity {
        require(scale.isFinite() && scale>0)
        return entries[(ordinal+when { scale<0.8f -> 1;scale>1.25f -> -1;else -> 0 }).coerceIn(0,entries.lastIndex)]
    }
}

/** Presentation preferences never write media metadata or schedule background work. */
internal class GalleryPresentation(context: Context) {
    private val preferences=context.getSharedPreferences("gallery-presentation",Context.MODE_PRIVATE)
    private val _density=MutableStateFlow(GridDensity.entries.single { it.columns==preferences.getInt("columns",3) })
    val density=_density.asStateFlow()
    private val _layout=MutableStateFlow(GalleryLayout.valueOf(requireNotNull(preferences.getString("layout",GalleryLayout.MOSAIC.name))))
    val layout=_layout.asStateFlow()
    private val writes=Mutex()
    suspend fun updateLayout(value: GalleryLayout)=withContext(Dispatchers.IO) {
        writes.withLock {
            CheckedPreferences(preferences).commit("Cannot save photo layout.") { putString("layout",value.name) }
            _layout.value=value
        }
    }
    suspend fun update(value: GridDensity)=withContext(Dispatchers.IO) {
        writes.withLock {
            CheckedPreferences(preferences).commit("Cannot save photo grid density.") { putInt("columns",value.columns) }
            _density.value=value
        }
    }
}

/** A date range is two matching identities, not a materialized library or row-count estimate. */
internal data class TimelineDateRange(val oldest: Long,val newest: Long) {
    init { require(oldest<=newest) }
    fun months(zone: ZoneId)=MonthNavigation(YearMonth.from(Instant.ofEpochMilli(oldest).atZone(zone)),YearMonth.from(Instant.ofEpochMilli(newest).atZone(zone)))
}

internal class MonthNavigation(val first: YearMonth,val last: YearMonth) {
    init { require(first<=last) }
    private fun serial(month: YearMonth)=month.year.toLong()*12+month.monthValue-1
    val span: Long=serial(last)-serial(first)
    fun month(fraction: Float): YearMonth {
        require(fraction.isFinite())
        return first.plusMonths((span*fraction.coerceIn(0f,1f)).roundToLong())
    }
    fun fraction(month: YearMonth): Float=if(span==0L) 0f else ((serial(month)-serial(first)).toDouble()/span).toFloat().coerceIn(0f,1f)
    fun clamp(month: YearMonth)=month.coerceIn(first,last)
    fun jumpDate(month: YearMonth): LocalDate=clamp(month).atEndOfMonth()
}

/** Visible-date presentation never fills missing dates from neighboring media or guesses a place. */
internal data class TimelineDateIndicator(val date: LocalDate?) {
    fun label(locale: java.util.Locale): String=date?.format(java.time.format.DateTimeFormatter.ofPattern("MMMM yyyy",locale)) ?: "Unknown date"
    fun description(locale: java.util.Locale): String=date?.format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.FULL).withLocale(locale)) ?: "Unknown date"
    companion object {
        fun from(millis: Long?,zone: ZoneId)=TimelineDateIndicator(millis?.let { Instant.ofEpochMilli(it).atZone(zone).toLocalDate() })
    }
}
