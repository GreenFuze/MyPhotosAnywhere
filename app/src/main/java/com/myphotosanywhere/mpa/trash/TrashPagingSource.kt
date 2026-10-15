package com.myphotosanywhere.mpa.trash

import androidx.paging.*
import androidx.room.InvalidationTracker
import androidx.room.withTransaction
import androidx.sqlite.db.SimpleSQLiteQuery
import com.myphotosanywhere.mpa.data.PhotoDatabase
import kotlinx.coroutines.CancellationException

internal data class TrashCursor(val time: Long,val key: String)
internal val DriveTrashRecord.cursor get()=TrashCursor(updatedAt,key)

internal class TrashPagingSource(private val db: PhotoDatabase): PagingSource<TrashCursor,DriveTrashRecord>() {
    companion object { val CONFIG=PagingConfig(60,18,false,90,300) }
    private val observer=object: InvalidationTracker.Observer("drive_trash","drive_sources") {
        override fun onInvalidated(tables: Set<String>)=invalidate()
    }
    init { db.invalidationTracker.addObserver(observer);registerInvalidatedCallback { db.invalidationTracker.removeObserver(observer) } }
    override fun getRefreshKey(state: PagingState<TrashCursor,DriveTrashRecord>)=
        state.anchorPosition?.let { state.closestItemToPosition(it)?.cursor }

    private suspend fun page(cursor: TrashCursor?,before: Boolean,limit: Int): List<DriveTrashRecord> {
        require(limit in 1..300)
        val predicate=if(cursor==null) "1" else if(before) "(t.updatedAt>? OR (t.updatedAt=? AND t.key<?))" else "(t.updatedAt<? OR (t.updatedAt=? AND t.key>?))"
        val order=if(before) "t.updatedAt ASC,t.key DESC" else "t.updatedAt DESC,t.key ASC"
        val args=if(cursor==null) arrayOf<Any>(limit) else arrayOf<Any>(cursor.time,cursor.time,cursor.key,limit)
        val rows=db.trash().entries(SimpleSQLiteQuery("""SELECT t.* FROM drive_trash t
            WHERE (t.confirmed=1 OR t.desired IS NOT NULL OR t.error IS NOT NULL)
            AND $predicate ORDER BY $order LIMIT ?""",args))
        return if(before) rows.reversed() else rows
    }

    override suspend fun load(params: LoadParams<TrashCursor>): LoadResult<TrashCursor,DriveTrashRecord> = try {
        val rows=db.withTransaction { when(params) {
            is LoadParams.Refresh -> if(params.key==null) page(null,false,params.loadSize) else {
                val cursor=requireNotNull(params.key)
                val center=db.trash().find(cursor.key)?.takeIf { (it.confirmed || it.desired!=null || it.error!=null) }
                val anchor=center?.cursor ?: cursor
                val before=page(anchor,true,params.loadSize/2)
                before+listOfNotNull(center)+page(anchor,false,params.loadSize-before.size-if(center==null) 0 else 1)
            }
            is LoadParams.Prepend -> page(params.key,true,params.loadSize)
            is LoadParams.Append -> page(params.key,false,params.loadSize)
        } }
        if(invalid) LoadResult.Invalid() else LoadResult.Page(rows,
            rows.firstOrNull()?.cursor?.takeIf { page(it,true,1).isNotEmpty() },
            rows.lastOrNull()?.cursor?.takeIf { page(it,false,1).isNotEmpty() })
    } catch(cancelled: CancellationException) { throw cancelled }
    catch(error: android.database.sqlite.SQLiteException) { LoadResult.Error(error) }
}
