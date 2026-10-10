package com.myphotosanywhere.mpa.edit

import android.app.Application
import android.graphics.Bitmap
import com.myphotosanywhere.mpa.drive.DriveBackupWorker
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[26],application=Application::class)
class PhotoRendererTest {
    @Test fun cropAndRotatePreserveOriginalBytesAndProduceExpectedDimensions() {
        val file=File.createTempFile("edit-source",".png",RuntimeEnvironment.getApplication().cacheDir)
        try {
            val original=Bitmap.createBitmap(120,80,Bitmap.Config.ARGB_8888)
            try { file.outputStream().use { original.compress(Bitmap.CompressFormat.PNG,100,it) } }
            finally { original.recycle() }
            val hash=DriveBackupWorker.digest(file,"SHA-256")
            val edited=PhotoRenderer.render(file,EditRecipe(left=.25f,right=.75f,quarterTurns=1),512)
            try { assertEquals(80,edited.width);assertEquals(60,edited.height) }
            finally { edited.recycle() }
            assertEquals(hash,DriveBackupWorker.digest(file,"SHA-256"))
        } finally { file.delete() }
    }
    @Test fun straightenKeepsBitmapBoundedAndCropsItsTransparentCorners() {
        val file=File.createTempFile("straighten",".png",RuntimeEnvironment.getApplication().cacheDir)
        try {
            val image=Bitmap.createBitmap(200,100,Bitmap.Config.ARGB_8888)
            try { file.outputStream().use { image.compress(Bitmap.CompressFormat.PNG,100,it) } } finally { image.recycle() }
            val edited=PhotoRenderer.render(file,EditRecipe(straighten=15f),512)
            try { assertTrue(edited.width<=200);assertTrue(edited.height<=100);assertTrue(edited.width>0 && edited.height>0) }
            finally { edited.recycle() }
        } finally { file.delete() }
    }
}
