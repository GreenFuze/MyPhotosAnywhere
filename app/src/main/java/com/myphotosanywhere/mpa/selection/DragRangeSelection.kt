package com.myphotosanywhere.mpa.selection

/** One gesture owns only its added range. Reversing never removes the pre-existing selection. */
internal class DragRangeSelection(initial: List<SelectionTarget>,window: List<SelectionTarget>,private val startKey: String) {
    private val baseline=initial.toList()
    private var ordered=window.toList()
    private var stopped=false
    private var last=initial.toList()
    init {
        require(initial.size<=MediaSelection.MAX_ITEMS && initial.map { it.key }.distinct().size==initial.size)
        check(window.map { it.key }.distinct().size==window.size && startKey in window.map { it.key })
        require(window.size<=MAX_VISITED)
    }
    data class Update(val targets: List<SelectionTarget>,val atLimit: Boolean=false,val cancelled: Boolean=false)
    fun move(window: List<SelectionTarget>,key: String): Update {
        if(stopped) return Update(last,cancelled=true)
        if(!merge(window)) { stopped=true;return Update(last,cancelled=true) }
        val start=ordered.indexOfFirst { it.key==startKey };val end=ordered.indexOfFirst { it.key==key }
        if(start<0 || end<0) { stopped=true;return Update(last,cancelled=true) }
        val next=baseline.toMutableList();var limited=false
        val indices=if(start<=end) start..end else start downTo end
        for(index in indices) {
            val item=ordered[index]
            if(next.none { it.matches(item) }) {
                if(next.size==MediaSelection.MAX_ITEMS) { limited=true;break }
                next.add(item)
            }
        }
        last=next.toList();return Update(last,atLimit=limited)
    }
    private fun merge(window: List<SelectionTarget>): Boolean {
        check(window.size<=MAX_VISITED && window.map { it.key }.distinct().size==window.size)
        if(window.isEmpty()) return false
        val indices=ordered.withIndex().associate { it.value.key to it.index }
        val shared=window.filter { it.key in indices }
        if(shared.isEmpty()) return false
        val first=requireNotNull(indices[shared.first().key]);val last=requireNotNull(indices[shared.last().key])
        if(last<first || ordered.subList(first,last+1).map { it.key }!=shared.map { it.key }) return false
        fun sameContent(a: SelectionTarget,b: SelectionTarget)=a.key==b.key && a.bytes==b.bytes && a.sha256==b.sha256 && a.md5==b.md5 && a.version==b.version
        if(shared.any { !sameContent(it,ordered[requireNotNull(indices[it.key])]) }) return false
        val windowFirst=window.indexOfFirst { it.key==shared.first().key };val windowLast=window.indexOfLast { it.key==shared.last().key }
        if(window.subList(windowFirst,windowLast+1).map { it.key }!=shared.map { it.key }) return false
        if(windowFirst>0 && first!=0 || windowLast<window.lastIndex && last!=ordered.lastIndex) return false
        val merged=window.take(windowFirst)+ordered+window.drop(windowLast+1)
        if(merged.size>MAX_VISITED) return false
        ordered=merged;return true
    }
    companion object { const val MAX_VISITED=1000 }
}

/** Smooth bounded speed in physical pixels; callers use monotonic frame deltas. */
internal class SelectionEdgeScroll(private val edge: Float,private val maximum: Float) {
    init { require(edge.isFinite() && edge>0 && maximum.isFinite() && maximum>0) }
    fun velocity(y: Float,top: Float,bottom: Float): Float {
        require(y.isFinite() && top.isFinite() && bottom.isFinite() && bottom>=top)
        val band=minOf(edge,(bottom-top)/2)
        if(band<=0) return 0f
        return when {
            y<top+band -> -maximum*((top+band-y)/band).coerceIn(0f,1f).let { it*it }
            y>bottom-band -> maximum*((y-bottom+band)/band).coerceIn(0f,1f).let { it*it }
            else -> 0f
        }
    }
}
