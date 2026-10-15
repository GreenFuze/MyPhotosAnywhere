package com.myphotosanywhere.mpa.trash

import android.app.Application
import androidx.paging.PagingSource
import androidx.room.Room
import androidx.room.withTransaction
import com.myphotosanywhere.mpa.data.PhotoDatabase
import com.myphotosanywhere.mpa.drive.DriveSource
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class TrashPagingSourceTest {
    private suspend fun seed(db: PhotoDatabase)=db.withTransaction {
        db.drive().saveSource(DriveSource("s","account","fixture@example.test","APP_FILES","root","Fixture"))
        repeat(240) { index ->
            db.trash().save(DriveTrashRecord("s:r$index","s","account","r$index",null,"$index.jpg","image/jpeg",8,"md5","sha","1","",1,
                confirmed=true,updatedAt=(index/2).toLong()))
        }
    }
    private suspend fun page(source: TrashPagingSource,params: PagingSource.LoadParams<TrashCursor>): PagingSource.LoadResult.Page<TrashCursor,DriveTrashRecord> {
        val result=source.load(params)
        check(result is PagingSource.LoadResult.Page) { "Expected a bounded keyset page; found $result" }
        return result
    }
    @Test fun equalTimestampForwardBackwardAndRefreshPagesHaveStableUniqueIdentities()=runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        try {
            seed(db)
            val source=TrashPagingSource(db)
            val initial=page(source,PagingSource.LoadParams.Refresh(null,90,false))
            assertEquals(90,initial.data.size)
            val next=page(source,PagingSource.LoadParams.Append(requireNotNull(initial.nextKey),60,false))
            assertEquals(60,next.data.size)
            assertEquals(150,(initial.data+next.data).map { it.key }.distinct().size)
            val previous=page(source,PagingSource.LoadParams.Prepend(requireNotNull(next.prevKey),60,false))
            assertEquals(initial.data.takeLast(60).map { it.key },previous.data.map { it.key })
            val anchor=next.data[20]
            val refreshed=page(source,PagingSource.LoadParams.Refresh(anchor.cursor,90,false))
            assertEquals(90,refreshed.data.size);assertEquals(90,refreshed.data.map { it.key }.distinct().size)
            assertTrue(refreshed.data.any { it.key==anchor.key })
            var after=next.nextKey;var gathered=initial.data+next.data
            while(after!=null) {
                val batch=page(source,PagingSource.LoadParams.Append(after,60,false))
                assertTrue(batch.data.size<=60);gathered=gathered+batch.data;after=batch.nextKey
            }
            assertEquals(240,gathered.size);assertEquals(240,gathered.map { it.key }.distinct().size)
            source.invalidate()
        } finally { db.close() }
    }
    @Test fun removedAnchorInvalidatesAndDisconnectedSourceRetainsCachedRecoveryEntries()=runBlocking<Unit> {
        val db=Room.inMemoryDatabaseBuilder(RuntimeEnvironment.getApplication(),PhotoDatabase::class.java).build()
        try {
            seed(db)
            val source=TrashPagingSource(db)
            val initial=page(source,PagingSource.LoadParams.Refresh(null,90,false))
            val anchor=initial.data[40]
            db.trash().save(anchor.copy(confirmed=false))
            withTimeout(5000) { while(!source.invalid) yield() }
            db.drive().disconnectSource("s")
            val replacement=TrashPagingSource(db)
            val refreshed=page(replacement,PagingSource.LoadParams.Refresh(anchor.cursor,90,false))
            assertTrue(refreshed.data.isNotEmpty());assertTrue(refreshed.data.none { it.key==anchor.key })
            assertEquals(refreshed.data.size,refreshed.data.map { it.key }.distinct().size)
            assertTrue(refreshed.data.size<=90)
            replacement.invalidate()
        } finally { db.close() }
    }
}
