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
    @Test fun floatingDateTracksDeviceDayAndMonthAtTimezoneBoundary() {
        val millis=Instant.parse("2026-03-01T00:30:00Z").toEpochMilli()
        val west=TimelineDateIndicator.from(millis,ZoneId.of("America/Los_Angeles"))
        val east=TimelineDateIndicator.from(millis,ZoneId.of("Asia/Jerusalem"))
        assertEquals("February 2026",west.label(java.util.Locale.US))
        assertEquals("Saturday, February 28, 2026",west.description(java.util.Locale.US))
        assertEquals("March 2026",east.label(java.util.Locale.US))
        assertNotEquals(west.date,east.date)
    }
    @Test fun floatingUnknownDateRemainsUnknown() {
        val value=TimelineDateIndicator.from(null,ZoneId.of("Asia/Jerusalem"))
        assertNull(value.date);assertEquals("Unknown date",value.label(java.util.Locale.US))
        assertEquals("Unknown date",value.description(java.util.Locale.US))
    }
    @Test fun floatingDateUsesChosenLocale() {
        val value=TimelineDateIndicator(java.time.LocalDate.of(2026,9,25))
        assertEquals("septembre 2026",value.label(java.util.Locale.FRANCE))
        assertEquals("vendredi 25 septembre 2026",value.description(java.util.Locale.FRANCE))
    }
}
