package com.myphotosanywhere.mpa.timeline

/** Only enough trailing space to retain a held photo when a short grid repacks. */
internal class TimelineAnchorClearance(heights: List<Int>,private val gap: Int,private val top: Int) {
    private val rows=heights.toList()
    init { require(rows.all { it>0 } && gap>=0 && top>=0) }
    fun bottom(anchorRow: Int?,savedOffset: Int,viewport: Int,normal: Int): Int {
        require(viewport>0 && normal>=0)
        if(anchorRow==null) return normal
        require(anchorRow in rows.indices)
        val offset=TimelineAnchorAlignment.visibleOffset(savedOffset,rows[anchorRow],viewport)
        val start=top.toLong()+rows.take(anchorRow).sumOf { it.toLong() }+anchorRow.toLong()*gap
        val scroll=start+offset
        if(scroll<=0) return normal
        val total=top.toLong()+rows.sumOf { it.toLong() }+maxOf(0,rows.size-1).toLong()*gap
        // One pixel absorbs integer measurement rounding. Clearance is bounded by one
        // viewport; a long library or an already reachable anchor keeps normal padding.
        return (scroll+viewport-total+1).coerceIn(normal.toLong(),maxOf(normal,viewport).toLong()).toInt()
    }
}
