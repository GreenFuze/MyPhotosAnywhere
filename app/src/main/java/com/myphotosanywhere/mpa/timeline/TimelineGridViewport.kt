package com.myphotosanywhere.mpa.timeline

import androidx.compose.foundation.lazy.grid.LazyGridState
import androidx.compose.runtime.*
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import kotlin.math.roundToInt

internal data class VisibleTimelineTile(val key: String,val index: Int,val offset: IntOffset,val size: IntSize)

/** Converts packed blocks to canonical tile coordinates for every scroll/return/date path. */
internal class TimelineGridViewport(val state: LazyGridState) {
    var blocks: BlockMosaicPlan? by mutableStateOf(null)
        private set
    private var scale by mutableFloatStateOf(1f)
    fun visible(): List<VisibleTimelineTile> {
        val plan=blocks
        return state.layoutInfo.visibleItemsInfo.flatMap { item ->
            if(plan==null) listOf(VisibleTimelineTile(item.key.toString(),item.index,item.offset,item.size))
            else (plan.blocks.getOrNull(item.index)?.takeIf { it.key==item.key } ?: plan.blocks.firstOrNull { it.key==item.key })?.tiles.orEmpty().map {
                VisibleTimelineTile(it.key,it.index,IntOffset(item.offset.x+(it.x*scale).roundToInt(),item.offset.y+(it.y*scale).roundToInt()),
                    IntSize((it.width*scale).roundToInt(),(it.height*scale).roundToInt()))
            }
        }.filter { it.offset.y+it.size.height>state.layoutInfo.viewportStartOffset && it.offset.y<state.layoutInfo.viewportEndOffset }
    }
    /** Capture old coordinates before installing new membership; the caller restores by ID. */
    fun install(next: BlockMosaicPlan?,pixelsPerDp: Float,preferred: TimelineAnchor?): TimelineAnchor? {
        val previous=blocks
        if(previous==null || next==null) { blocks=next;scale=pixelsPerDp;return null }
        val visible=visible()
        val held=TimelineViewport.choose(visible.map { TimelineAnchor(TimelineCursor(null,it.key),-it.offset.y) },preferred)
        val old=held?.let { previous.locations[it.cursor.key] }
        val fresh=held?.let { next.locations[it.cursor.key] }
        val changed=old!=null && fresh!=null &&
            (previous.blocks[old.first].key!=next.blocks[fresh.first].key || old.second.x!=fresh.second.x || old.second.y!=fresh.second.y || old.second.height!=fresh.second.height || old.second.width!=fresh.second.width)
        blocks=next;scale=pixelsPerDp
        return held.takeIf { changed }
    }
    private fun placement(index: Int,offset: Int): Pair<Int,Int> {
        val plan=blocks ?: return index to offset
        val tile=plan.tiles.getOrNull(index) ?: return index.coerceAtMost(maxOf(0,plan.blocks.lastIndex)) to offset
        val location=requireNotNull(plan.locations[tile.key])
        return location.first to offset+(tile.y*scale).roundToInt()
    }
    suspend fun scrollToItem(index: Int,offset: Int=0) { val position=placement(index,offset);state.scrollToItem(position.first,position.second) }
    fun requestScrollToItem(index: Int,offset: Int=0) { val position=placement(index,offset);state.requestScrollToItem(position.first,position.second) }
}
