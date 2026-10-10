package com.myphotosanywhere.mpa.library

import android.graphics.Rect
import android.graphics.RectF
import android.app.Application
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class OriginalImageGeometryTest {
    @Test fun allExifOrientationsFitAndRoundTripTheRawPixels() {
        for(orientation in 1..8) {
            val matrix=OriginalImageGeometry.matrix(4000,3000,orientation,1080,1920,1f,0f,0f)
            val rect=RectF(0f,0f,4000f,3000f);matrix.mapRect(rect)
            assertEquals(1080f,rect.width(),.1f)
            assertEquals(540f,rect.centerX(),.1f);assertEquals(960f,rect.centerY(),.1f)
            val inverse=android.graphics.Matrix();assertTrue(matrix.invert(inverse))
            val point=floatArrayOf(1234f,2345f);matrix.mapPoints(point);inverse.mapPoints(point)
            assertEquals(1234f,point[0],.01f);assertEquals(2345f,point[1],.01f)
        }
    }
    @Test fun zoomedRegionsDecodeNativePixelsAndLargeRegionsStayBounded() {
        assertEquals(1,OriginalImageGeometry.sample(1.5f,Rect(0,0,1080,1920)))
        assertEquals(4,OriginalImageGeometry.sample(.2f,Rect(0,0,4000,3000)))
        val sample=OriginalImageGeometry.sample(1f,Rect(0,0,20000,10000))
        assertTrue(20000L*10000/(sample.toLong()*sample)<=4_000_000)
    }
}
