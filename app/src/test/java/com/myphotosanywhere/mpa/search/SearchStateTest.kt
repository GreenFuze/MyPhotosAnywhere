package com.myphotosanywhere.mpa.search

import android.app.Application
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.ViewModelStore
import com.myphotosanywhere.mpa.timeline.*
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.LocalDate
import java.time.ZoneId

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
@OptIn(ExperimentalCoroutinesApi::class)
class SearchStateTest {
    @Test fun dateRangesUseLocalInclusiveDaysAcrossDst() {
        val day=LocalDate.of(2026,3,29)
        val query=SearchCriteria(dates=SearchDates(day,day)).timeline(ZoneId.of("Europe/Berlin"))
        assertEquals(23*60*60*1000L,requireNotNull(query.untilMillis)-requireNotNull(query.fromMillis))
        assertEquals("",query.search);assertEquals(TimelineMediaType.ALL,query.type)
    }
    @Test fun savedCriteriaRetainEveryIndependentFilter() {
        val value=SearchCriteria("כינרת",TimelineMediaType.VIDEOS,"Favorites","account:folder",SearchDates(LocalDate.of(2026,1,1),LocalDate.of(2026,2,3)))
        assertEquals(value,SearchCriteria.restore(value.save()));assertTrue(value.filtered);assertTrue(value.browsing)
    }
    @Test(expected=IllegalArgumentException::class) fun reverseDateRangeFailsFast() { SearchDates(LocalDate.of(2026,2,1),LocalDate.of(2026,1,1)) }
    @Test(expected=IllegalArgumentException::class) fun oversizedPersistedQueryFailsFast() { SearchCriteria("x".repeat(257)) }
    @Test fun recentsAreBoundedDeduplicatedAndClearable() {
        val recent=RecentQueries();repeat(12) { recent.add("query-$it") };assertEquals(8,recent.snapshot().size)
        recent.add(" QUERY-11 ");assertEquals("QUERY-11",recent.snapshot().first());assertEquals(8,recent.snapshot().size)
        recent.add(" ");assertEquals(8,recent.snapshot().size);recent.clear();assertTrue(recent.snapshot().isEmpty())
    }
    @Test(expected=IllegalArgumentException::class) fun invalidSavedRecentStateFailsFast() { RecentQueries(List(9) { "query-$it" }) }
    private fun modelTest(block: suspend TestScope.(SearchViewModel,SavedStateHandle)->Unit)=runTest {
        Dispatchers.setMain(StandardTestDispatcher(testScheduler))
        val saved=SavedStateHandle();val model=SearchViewModel(saved);val store=ViewModelStore();store.put("search",model)
        try { block(model,saved) } finally { store.clear();Dispatchers.resetMain() }
    }
    @Test fun typingIsDebouncedAndCancelsSupersededCriteria()=modelTest { model,_ ->
        runCurrent();model.text("first");runCurrent();advanceTimeBy(200);model.text("second");runCurrent()
        advanceTimeBy(299);runCurrent();assertEquals("",model.applied.value.text)
        advanceTimeBy(1);runCurrent();assertEquals("second",model.applied.value.text)
    }
    @Test fun explicitSubmitIsImmediateAndRestoresQueryAndRecents()=modelTest { model,saved ->
        model.text("beach");model.submit();assertEquals("beach",model.applied.value.text)
        val restored=SearchViewModel(saved);val store=ViewModelStore();store.put("restored",restored)
        try { assertEquals(model.draft.value,restored.draft.value);assertEquals(listOf("beach"),restored.recents.value) } finally { store.clear() }
    }
    @Test fun clearingFiltersRetainsQueryAndNeverChangesDefaultPhotos()=modelTest { model,_ ->
        val photos=TimelineQuery();model.update(SearchCriteria("beach",TimelineMediaType.VIDEOS,"Favorites","source"));model.clearFilters()
        assertEquals(SearchCriteria("beach"),model.applied.value);assertEquals(TimelineQuery(),photos)
    }
    @Test fun longInputReportsAnExplicitLimitWithoutTruncatingThePreviousQuery()=modelTest { model,_ ->
        model.text("previous");model.text("x".repeat(257));assertEquals("previous",model.draft.value.text);assertNotNull(model.notice.value)
    }
    @Test fun clearingHistoryLeavesTheCurrentQueryUsable()=modelTest { model,_ ->
        model.text("beach");model.submit();model.clearHistory();assertTrue(model.recents.value.isEmpty());assertEquals("beach",model.draft.value.text)
    }
}
