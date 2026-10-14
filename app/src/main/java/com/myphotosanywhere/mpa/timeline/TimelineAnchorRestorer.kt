package com.myphotosanywhere.mpa.timeline

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.transformLatest

/** A new retained identity snapshot cancels any placement based on its obsolete row index. */
internal interface TimelinePlacement {
    suspend fun place(index: Int,offset: Int)
    suspend fun align(key: String,offset: Int): Boolean
}

internal class TimelineAnchorRestorer(private val placement: TimelinePlacement) {
    @OptIn(ExperimentalCoroutinesApi::class)
    suspend fun restore(anchor: TimelineAnchor,snapshots: Flow<List<String>>): Boolean =
        snapshots.distinctUntilChanged().transformLatest { keys ->
            val index=keys.indexOf(anchor.cursor.key)
            if(index<0) emit(false) else {
                repeat(MAX_ATTEMPTS) {
                    placement.place(index,anchor.offset)
                    if(placement.align(anchor.cursor.key,anchor.offset)) { emit(true);return@transformLatest }
                }
                error("Canonical anchor ${anchor.cursor.key} could not be placed after $MAX_ATTEMPTS completed layout attempts")
            }
        }.first()
    private companion object { const val MAX_ATTEMPTS=8 }
}

/** A viewport edge may make the saved offset unreachable even while its photo is visible. */
internal object TimelineAnchorAlignment {
    fun satisfied(actualY: Int,savedOffset: Int,canScrollForward: Boolean,canScrollBackward: Boolean): Boolean {
        val correction=actualY.toLong()+savedOffset
        return kotlin.math.abs(correction)<=1 || correction>0 && !canScrollForward || correction<0 && !canScrollBackward
    }
}
