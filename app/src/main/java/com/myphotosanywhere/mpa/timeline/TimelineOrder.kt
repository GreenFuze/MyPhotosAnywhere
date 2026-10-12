package com.myphotosanywhere.mpa.timeline

/** Matches SQLite's timestamp-descending / UTF-8 binary-key-ascending ordering. */
internal object TimelineOrder: Comparator<TimelineCursor> {
    override fun compare(a: TimelineCursor,b: TimelineCursor): Int {
        val time=when {
            a.millis==null && b.millis==null -> 0
            a.millis==null -> 1
            b.millis==null -> -1
            else -> b.millis.compareTo(a.millis)
        }
        if(time!=0) return time
        val left=a.key.toByteArray(Charsets.UTF_8)
        val right=b.key.toByteArray(Charsets.UTF_8)
        for(index in 0 until minOf(left.size,right.size)) {
            val difference=(left[index].toInt() and 255)-(right[index].toInt() and 255)
            if(difference!=0) return difference
        }
        return left.size.compareTo(right.size)
    }
}
