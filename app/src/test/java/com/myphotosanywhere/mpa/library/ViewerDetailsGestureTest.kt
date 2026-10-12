package com.myphotosanywhere.mpa.library

import org.junit.Assert.*
import org.junit.Test

class ViewerDetailsGestureTest {
    private fun gesture()=ViewerDetailsGesture(72f,8f)
    @Test fun tapAndShortDragDoNotOpenDetails() {
        val tap=gesture();assertFalse(tap.move(0f,3f,1,false,false));assertFalse(tap.finish())
        val short=gesture();assertTrue(short.move(0f,30f,1,false,false));assertFalse(short.finish())
    }
    @Test fun downwardDragOpensDetailsOnlyAfterThreshold() {
        val drag=gesture()
        assertFalse(drag.move(1f,5f,1,false,false))
        assertTrue(drag.move(2f,30f,1,false,false));assertFalse(drag.finish())
        assertTrue(drag.move(0f,37f,1,false,true));assertTrue(drag.finish())
    }
    @Test fun horizontalPagingOwnsEntireGestureEvenIfItLaterTurnsDown() {
        val drag=gesture();assertFalse(drag.move(-20f,2f,1,false,false))
        assertFalse(drag.move(0f,150f,1,false,false));assertFalse(drag.finish())
    }
    @Test fun upwardDragDoesNotOpenDetails() {
        val drag=gesture();assertFalse(drag.move(0f,-100f,1,false,false));assertFalse(drag.finish())
    }
    @Test fun zoomedPanNeverOpensDetails() {
        val drag=gesture();assertFalse(drag.move(0f,150f,1,true,false));assertFalse(drag.finish())
    }
    @Test fun secondFingerCancelsAnAlreadyClaimedDetailsDrag() {
        val drag=gesture();assertTrue(drag.move(0f,20f,1,false,false))
        assertFalse(drag.move(0f,100f,2,false,false))
        assertFalse(drag.move(0f,100f,1,false,false));assertFalse(drag.finish())
    }
    @Test fun childOwnedDragIsNotStolen() {
        val drag=gesture();assertFalse(drag.move(0f,100f,1,false,true));assertFalse(drag.finish())
    }
    @Test fun reversingBelowThresholdCancelsOpening() {
        val drag=gesture();assertTrue(drag.move(0f,100f,1,false,false))
        assertTrue(drag.move(0f,-70f,1,false,true));assertFalse(drag.finish())
    }
    @Test(expected=IllegalArgumentException::class) fun invalidThresholdFailsFast() { ViewerDetailsGesture(8f,8f) }
    @Test(expected=IllegalArgumentException::class) fun nonFiniteMotionFailsFast() { gesture().move(Float.NaN,0f,1,false,false) }
}
