package com.myphotosanywhere.mpa.timeline

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.time.YearMonth

/** Top is newest. A scrub represents calendar months, never an estimated media index. */
internal class DateScrubScale(val months: MonthNavigation) {
    fun month(fraction: Float): YearMonth {
        require(fraction.isFinite())
        return months.month(1-fraction.coerceIn(0f,1f))
    }
    fun fraction(month: YearMonth)=1-months.fraction(month)
    fun fraction(y: Float,top: Float,bottom: Float): Float {
        require(y.isFinite() && top.isFinite() && bottom.isFinite() && bottom>=top)
        return if(bottom==top) 0f else ((y-top)/(bottom-top)).coerceIn(0f,1f)
    }
}

internal data class DateScrubState(val month: YearMonth?=null,val dragging: Boolean=false,val pending: Boolean=false)

/** Owns exactly one cancellable seek; release flushes the most recent month immediately. */
internal class DateScrubCoordinator(private val scope: CoroutineScope,private val seek: suspend (YearMonth)->Unit,
    private val debounceMillis: Long=100) {
    init { require(debounceMillis>=0) }
    private val _state=MutableStateFlow(DateScrubState())
    val state=_state.asStateFlow()
    private var job: Job?=null
    private var generation=0L
    fun offer(month: YearMonth) {
        if(_state.value.dragging && _state.value.month==month) return
        schedule(month,dragging=true,wait=debounceMillis)
    }
    fun finish() {
        val current=_state.value
        if(current.pending) schedule(requireNotNull(current.month),dragging=false,wait=0)
        else _state.value=current.copy(dragging=false)
    }
    fun cancel() { generation++;job?.cancel();job=null;_state.value=DateScrubState() }
    private fun schedule(month: YearMonth,dragging: Boolean,wait: Long) {
        job?.cancel();val ticket=++generation
        _state.value=DateScrubState(month,dragging,pending=true)
        job=scope.launch {
            delay(wait);seek(month);currentCoroutineContext().ensureActive()
            if(ticket==generation) _state.value=_state.value.copy(pending=false)
        }
    }
}
