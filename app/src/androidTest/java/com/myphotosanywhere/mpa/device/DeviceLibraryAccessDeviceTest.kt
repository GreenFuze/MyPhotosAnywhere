package com.myphotosanywhere.mpa.device

import android.content.ContentValues
import android.graphics.Bitmap
import android.graphics.Color
import android.os.Build
import android.provider.MediaStore
import androidx.core.content.edit
import androidx.core.net.toUri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Prepared at the end of the full-access invocation, then checked after external revocation. */
@Target(AnnotationTarget.CLASS)
@Retention(AnnotationRetention.RUNTIME)
annotation class DeviceLibraryStage

internal object DeviceAccessFixture {
    suspend fun prepare(app: MpaApplication)=withContext(Dispatchers.IO) {
        check(app.deviceMedia.permissions.current().full)
        val saved=JSONArray()
        repeat(2) { index ->
            val values=ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME,"mpa-revocation-$index.png")
                put(MediaStore.MediaColumns.MIME_TYPE,"image/png")
                put(MediaStore.MediaColumns.RELATIVE_PATH,"DCIM/Camera")
                put(MediaStore.MediaColumns.IS_PENDING,1)
                put(MediaStore.Images.ImageColumns.DATE_TAKEN,13_100_000_000_000L+index)
            }
            val uri=requireNotNull(app.contentResolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values))
            val image=Bitmap.createBitmap(120,80,Bitmap.Config.ARGB_8888).apply { eraseColor(Color.BLUE) }
            try { requireNotNull(app.contentResolver.openOutputStream(uri)).use { check(image.compress(Bitmap.CompressFormat.PNG,100,it)) } }
            finally { image.recycle() }
            app.contentResolver.update(uri,ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING,0) },null,null)
            app.deviceMedia.reconcile()
            val mediaId=android.content.ContentUris.parseId(uri)
            val id=app.database.openHelper.readableDatabase.query("SELECT photoId FROM device_media WHERE uri LIKE ? AND available=1",arrayOf("%/$mediaId")).use {
                check(it.moveToFirst());it.getString(0)
            }
            val photo=if(index==1) app.deviceMedia.materialize(id) else requireNotNull(app.database.photos().find(id))
            saved.put(JSONObject().put("id",id).put("uri",uri.toString()).put("path",photo.originalPath)
                .put("sha",photo.sha256).put("bytes",photo.bytes).put("captured",photo.capturedAt ?: JSONObject.NULL))
        }
        app.getSharedPreferences("device-revocation-fixture",0).edit(commit=true) { putString("rows",saved.toString()) }
    }
}

/** CI revokes permissions between instrumentation invocations, since revocation kills the app. */
@RunWith(AndroidJUnit4::class)
@DeviceLibraryStage
class DeviceLibraryAccessDeviceTest {
    @Test fun externalRevocationKeepsMetadataAndOwnedOriginalsThenSelectedGrantIsRechecked()=runBlocking<Unit> {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val app=instrumentation.targetContext.applicationContext as MpaApplication
        val prefs=app.getSharedPreferences("device-revocation-fixture",0)
        val rows=JSONArray(requireNotNull(prefs.getString("rows",null)))
        assertEquals(2,rows.length());assertFalse("CI must revoke photo access before this invocation",app.deviceMedia.permissions.current().granted)
        try {
            app.deviceMedia.reconcile()
            val keys=app.timeline.page(TimelineQuery(),null,TimelineDirection.AFTER,300).map { it.key }.toSet()
            for(index in 0 until rows.length()) {
                val saved=rows.getJSONObject(index);val id=saved.getString("id")
                val photo=requireNotNull(app.database.photos().find(id));val reference=requireNotNull(app.database.deviceMedia().find(id))
                assertFalse(reference.available);assertFalse(app.deviceMedia.readable(id))
                assertEquals(saved.getString("sha"),photo.sha256);assertEquals(saved.getLong("bytes"),photo.bytes)
                assertEquals(saved.getString("path"),photo.originalPath)
                if(saved.isNull("captured")) assertNull(photo.capturedAt)
                else assertEquals(saved.getLong("captured"),requireNotNull(photo.capturedAt))
                if(index==0) { assertEquals("",photo.originalPath);assertFalse("photo:$id" in keys) }
                else {
                    assertTrue("photo:$id" in keys);assertTrue(File(photo.originalPath).isFile)
                    assertEquals(photo.sha256,com.myphotosanywhere.mpa.drive.DriveBackupWorker.digest(File(photo.originalPath),"SHA-256"))
                }
            }
            if(Build.VERSION.SDK_INT>=34) {
                instrumentation.uiAutomation.grantRuntimePermission(app.packageName,android.Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
                val selected=app.deviceMedia.permissions.current()
                assertTrue(selected.selected);assertFalse(selected.full);assertFalse(selected.images);assertFalse(selected.videos)
                assertTrue(android.Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED in app.deviceMedia.permissions.request())
            } else if(Build.VERSION.SDK_INT>=33) {
                assertEquals(setOf(android.Manifest.permission.READ_MEDIA_IMAGES,android.Manifest.permission.READ_MEDIA_VIDEO),app.deviceMedia.permissions.request().toSet())
            } else assertEquals(listOf(android.Manifest.permission.READ_EXTERNAL_STORAGE),app.deviceMedia.permissions.request().toList())
        } finally {
            for(index in 0 until rows.length()) {
                val saved=rows.getJSONObject(index);val id=saved.getString("id")
                app.contentResolver.delete(saved.getString("uri").toUri(),null,null)
                app.database.openHelper.writableDatabase.execSQL("DELETE FROM photos WHERE id=?",arrayOf(id))
                saved.getString("path").takeIf { it.isNotBlank() }?.let { path ->
                    val file=File(path);check(file.canonicalFile.parentFile==File(app.filesDir,"originals").canonicalFile);check(file.name.startsWith("device-"));file.delete()
                }
            }
            prefs.edit(commit=true) { clear() }
        }
    }
}
