package com.myphotosanywhere.mpa.device

import android.app.Application
import android.database.sqlite.SQLiteDatabase
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class DeviceMediaProjectionTest {
    @Test fun androidEightImageAndVideoViewsSupportBothBoundedDiscoveryQueries() {
        SQLiteDatabase.create(null).use { db ->
            // Android 8.1 MediaProvider's actual legacy views omit media_type and video orientation.
            db.execSQL("CREATE TABLE files (_id INTEGER,media_type INTEGER,_display_name TEXT,mime_type TEXT,_size INTEGER,date_added INTEGER,datetaken INTEGER,date_modified INTEGER,width INTEGER,height INTEGER,orientation INTEGER,bucket_id TEXT,bucket_display_name TEXT)")
            val common="_id,_display_name,mime_type,_size,date_added,datetaken,date_modified,width,height,bucket_id,bucket_display_name"
            db.execSQL("CREATE VIEW images AS SELECT $common,orientation FROM files WHERE media_type=1")
            db.execSQL("CREATE VIEW video AS SELECT $common FROM files WHERE media_type=3")
            db.execSQL("INSERT INTO files VALUES(1,1,'Camera.jpg','image/jpeg',100,1,1000,1,100,200,90,'camera','Camera')")
            db.execSQL("INSERT INTO files VALUES(2,3,'Video.mp4','video/mp4',200,2,2000,2,400,300,NULL,'movies','Movies')")
            for(sdk in listOf(26,27,28)) for(video in listOf(false,true)) {
                val projection=DeviceMediaProjection(video,sdk)
                db.query(if(video) "video" else "images",projection.columns.toTypedArray(),projection.selection,arrayOf("0"),null,null,"_id ASC","120").use { cursor ->
                    assertTrue(cursor.moveToFirst());assertEquals(if(video) "Video.mp4" else "Camera.jpg",cursor.getString(1));assertFalse(cursor.moveToNext())
                    assertEquals(if(video) -1 else 9,cursor.getColumnIndex("orientation"))
                }
            }
        }
    }
    @Test fun modernProjectionUsesSupportedAvailabilityAndDirectoryColumnsWithoutRedundantKindPredicates() {
        for(sdk in listOf(29,35,37)) for(video in listOf(false,true)) {
            val projection=DeviceMediaProjection(video,sdk)
            assertTrue("orientation" in projection.columns);assertTrue("relative_path" in projection.columns)
            assertTrue(projection.selection.contains("is_pending=0"));assertFalse(projection.selection.contains("media_type"))
            assertEquals(sdk>=30,"generation_modified" in projection.columns)
            assertEquals(sdk>=30,projection.selection.contains("is_trashed=0"))
        }
    }
}
