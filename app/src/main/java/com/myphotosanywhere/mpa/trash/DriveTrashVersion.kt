package com.myphotosanywhere.mpa.trash

/** Exact monotonic server versions; unknown ordering cannot overturn a known verified revision. */
internal object DriveTrashVersion {
    fun notOlder(current: String?,incoming: String?): Boolean {
        if(current==null || current==incoming) return true
        val before=current.toBigIntegerOrNull() ?: return false
        val after=incoming?.toBigIntegerOrNull() ?: return false
        return after>=before
    }
}
