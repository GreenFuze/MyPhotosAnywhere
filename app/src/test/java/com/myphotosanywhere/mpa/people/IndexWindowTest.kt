package com.myphotosanywhere.mpa.people

import java.time.*
import org.junit.Assert.*
import org.junit.Test

class IndexWindowTest {
    @Test fun normalWindowIsInclusiveAtStartExclusiveAtEnd() {
        assertFalse(IndexWindow.contains(59,60,360)); assertTrue(IndexWindow.contains(60,60,360))
        assertTrue(IndexWindow.contains(359,60,360)); assertFalse(IndexWindow.contains(360,60,360))
    }
    @Test fun midnightWindowIncludesBothDaysWithoutScanningAtNoon() {
        assertTrue(IndexWindow.contains(23*60,22*60,6*60)); assertTrue(IndexWindow.contains(5*60,22*60,6*60))
        assertFalse(IndexWindow.contains(12*60,22*60,6*60)); assertFalse(IndexWindow.contains(6*60,22*60,6*60))
    }
    @Test fun delayIsZeroInWindowAndUsesNextLocalStartOutsideIt() {
        val now=ZonedDateTime.parse("2026-10-10T12:00:00+03:00[Asia/Jerusalem]")
        assertEquals(Duration.ofHours(10),IndexWindow.delay(now,22*60,6*60))
        assertEquals(Duration.ZERO,IndexWindow.delay(now.plusHours(12),22*60,6*60))
    }
    @Test fun springAndFallTransitionsUseElapsedTimeToNextWallClockStart() {
        val zone=ZoneId.of("Europe/Berlin")
        val spring=ZonedDateTime.of(2026,3,29,0,0,0,0,zone)
        assertEquals(Duration.ofHours(3),IndexWindow.delay(spring,4*60,6*60))
        val fall=ZonedDateTime.of(2026,10,25,0,0,0,0,zone)
        assertEquals(Duration.ofHours(5),IndexWindow.delay(fall,4*60,6*60))
    }
    @Test fun equalOrOutOfBoundsWindowsAreRejected() {
        assertThrows(IllegalArgumentException::class.java) { IndexWindow.contains(0,0,0) }
        assertThrows(IllegalArgumentException::class.java) { IndexWindow.contains(1440,0,60) }
    }
}
