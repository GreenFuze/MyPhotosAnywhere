package com.myphotosanywhere.mpa.core

/** Plans deletion of disposable downloads only; callers must recheck pins under a lock. */
data class CachedFile(
    val id: String,
    val bytes: Long,
    val lastAccessMillis: Long,
    val verifiedRemoteCopy: Boolean,
    val pinned: Boolean = false,
    val pendingWrite: Boolean = false,
    val activeReaders: Int = 0,
)

data class EvictionPlan(val ids: List<String>, val remainingBytes: Long, val budgetSatisfied: Boolean)

object CachePolicy {
    fun plan(files: List<CachedFile>, budgetBytes: Long): EvictionPlan {
        require(budgetBytes >= 0)
        require(files.map { it.id }.distinct().size == files.size)
        require(files.all { it.bytes >= 0 && it.activeReaders >= 0 })
        var remaining = files.fold(0L) { sum, file -> Math.addExact(sum, file.bytes) }
        val removed = mutableListOf<String>()
        files.filter { it.verifiedRemoteCopy && !it.pinned && !it.pendingWrite && it.activeReaders == 0 }
            .sortedWith(compareBy<CachedFile> { it.lastAccessMillis }.thenBy { it.id })
            .forEach { file ->
                if (remaining > budgetBytes) {
                    remaining -= file.bytes
                    removed += file.id
                }
            }
        return EvictionPlan(removed, remaining, remaining <= budgetBytes)
    }
}
