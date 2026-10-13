package com.myphotosanywhere.mpa.timeline

import android.app.Application
import android.content.Context
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.time.YearMonth
import java.time.ZoneId
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class GalleryPresentationTest {
    @Test fun densityClampsAndChangesOnlyOneStepPerPinch() {
        assertEquals(GridDensity.LARGE,GridDensity.DEFAULT.pinch(4f))
        assertEquals(GridDensity.COMPACT,GridDensity.DEFAULT.pinch(0.1f))
        assertEquals(GridDensity.DEFAULT,GridDensity.DEFAULT.pinch(1.1f))
        assertEquals(GridDensity.LARGE,GridDensity.LARGE.pinch(2f))
        assertEquals(GridDensity.COMPACT,GridDensity.COMPACT.pinch(0.1f))
    }
    @Test fun invalidGestureScalesFailFast() {
        for(value in listOf(Float.NaN,Float.POSITIVE_INFINITY,0f,-1f)) assertThrows(IllegalArgumentException::class.java) { GridDensity.DEFAULT.pinch(value) }
    }
    @Test fun densityPersistsWithoutChangingStorageConsent()=runBlocking<Unit> {
        val context=RuntimeEnvironment.getApplication()
        val preferences=context.getSharedPreferences("gallery-presentation",Context.MODE_PRIVATE)
        check(preferences.edit().clear().commit())
        val storage=com.myphotosanywhere.mpa.core.LibrarySettings(context).state.value
        try {
            assertEquals(GridDensity.DEFAULT,GalleryPresentation(context).density.value)
            GalleryPresentation(context).update(GridDensity.COMPACT)
            assertEquals(GridDensity.COMPACT,GalleryPresentation(context).density.value)
            assertEquals(storage,com.myphotosanywhere.mpa.core.LibrarySettings(context).state.value)
        } finally { check(preferences.edit().clear().commit()) }
    }
    @Test fun corruptedDensityIsNotSilentlyAccepted() {
        val preferences=RuntimeEnvironment.getApplication().getSharedPreferences("gallery-presentation",Context.MODE_PRIVATE)
        try { check(preferences.edit().putInt("columns",99).commit());assertThrows(NoSuchElementException::class.java) { GalleryPresentation(RuntimeEnvironment.getApplication()) } }
        finally { check(preferences.edit().clear().commit()) }
    }
    @Test fun monthScrubHasCorrectEndpointsAndRoundTrips() {
        val range=MonthNavigation(YearMonth.of(1998,2),YearMonth.of(2026,10))
        assertEquals(range.first,range.month(-1f));assertEquals(range.last,range.month(2f))
        for(i in 0..range.span) { val month=range.first.plusMonths(i);assertEquals(month,range.month(range.fraction(month))) }
    }
    @Test fun sameMonthAndLeapYearJumpRemainValid() {
        val month=YearMonth.of(2024,2);val range=MonthNavigation(month,month)
        assertEquals(0L,range.span);assertEquals(month,range.month(1f));assertEquals("2024-02-29",range.jumpDate(month).toString())
    }
    @Test fun deviceTimezoneDeterminesMonthWithoutChangingTimestamp() {
        val millis=Instant.parse("2026-03-01T00:30:00Z").toEpochMilli()
        val range=TimelineDateRange(millis,millis)
        assertEquals(YearMonth.of(2026,2),range.months(ZoneId.of("America/Los_Angeles")).first)
        assertEquals(YearMonth.of(2026,3),range.months(ZoneId.of("Asia/Jerusalem")).first)
        assertEquals(millis,range.oldest)
    }
    @Test fun impossibleDateRangesAndNonfiniteScrubsFailFast() {
        assertThrows(IllegalArgumentException::class.java) { TimelineDateRange(2,1) }
        assertThrows(IllegalArgumentException::class.java) { MonthNavigation(YearMonth.of(2026,1),YearMonth.of(2025,1)) }
        assertThrows(IllegalArgumentException::class.java) { MonthNavigation(YearMonth.of(2026,1),YearMonth.of(2026,1)).month(Float.NaN) }
    }
}
