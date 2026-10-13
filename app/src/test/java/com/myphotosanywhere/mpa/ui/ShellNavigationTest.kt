package com.myphotosanywhere.mpa.ui

import androidx.compose.runtime.saveable.SaverScope
import org.junit.Assert.*
import org.junit.Test

class ShellNavigationTest {
    @Test fun backReturnsToCollectionOriginInsteadOfClosingTheApp() {
        val nav=ShellNavigation()
        nav.select(ShellDestination.COLLECTIONS)
        nav.open(ShellDestination.PEOPLE)
        nav.open(ShellDestination.PHOTOS)
        assertEquals(ShellDestination.COLLECTIONS,nav.primary)
        nav.back()
        assertEquals(ShellDestination.PEOPLE,nav.destination)
        nav.back()
        assertEquals(ShellDestination.COLLECTIONS,nav.destination)
        nav.back()
        assertEquals(ShellDestination.PHOTOS,nav.destination)
        assertFalse(nav.canGoBack)
    }
    @Test fun repeatedNotificationEntryDoesNotStackDuplicateStorageScreens() {
        val nav=ShellNavigation()
        nav.select(ShellDestination.SEARCH)
        repeat(3) { nav.open(ShellDestination.STORAGE) }
        nav.back()
        assertEquals(ShellDestination.SEARCH,nav.destination)
    }
    @Test fun recreationRestoresOriginAndCurrentDestination() {
        val nav=ShellNavigation()
        nav.select(ShellDestination.COLLECTIONS)
        nav.open(ShellDestination.PLACES)
        nav.open(ShellDestination.STORAGE)
        val scope=object: SaverScope { override fun canBeSaved(value: Any)=true }
        val saved=with(ShellNavigation.Saver) { requireNotNull(scope.save(nav)) }
        val restored=requireNotNull(ShellNavigation.Saver.restore(saved))
        assertEquals(ShellDestination.STORAGE,restored.destination)
        restored.back()
        assertEquals(ShellDestination.PLACES,restored.destination)
        assertEquals(ShellDestination.COLLECTIONS,restored.primary)
    }
    @Test(expected=IllegalArgumentException::class)
    fun childCannotBecomePrimaryDestination() { ShellNavigation().select(ShellDestination.STORAGE) }
    @Test(expected=IllegalArgumentException::class)
    fun emptyRestoredHistoryFailsFast() { ShellNavigation(emptyList()) }

    @Test fun searchBackRestoresTheExactCollectionContext() {
        val nav=ShellNavigation();nav.select(ShellDestination.COLLECTIONS);nav.open(ShellDestination.PHOTOS)
        nav.select(ShellDestination.SEARCH);assertEquals(ShellDestination.SEARCH,nav.activePrimary)
        nav.back();assertEquals(ShellDestination.PHOTOS,nav.destination);assertEquals(ShellDestination.COLLECTIONS,nav.primary)
    }
    @Test fun repeatedSearchSelectionDoesNotStackAndChildrenReturnToSearch() {
        val nav=ShellNavigation();repeat(3) { nav.select(ShellDestination.SEARCH) };nav.open(ShellDestination.PEOPLE)
        nav.back();assertEquals(ShellDestination.SEARCH,nav.destination);nav.back();assertEquals(ShellDestination.PHOTOS,nav.destination)
        assertFalse(nav.canGoBack)
    }
    @Test fun managementChildrenRestoreSearchOriginAfterRepeatedActivityEntry() {
        val nav=ShellNavigation();nav.select(ShellDestination.COLLECTIONS);nav.select(ShellDestination.SEARCH)
        repeat(3) { nav.open(ShellDestination.ACTIVITY) };nav.open(ShellDestination.IMPORT_HISTORY)
        nav.back();assertEquals(ShellDestination.ACTIVITY,nav.destination);nav.back();assertEquals(ShellDestination.SEARCH,nav.destination)
        nav.back();assertEquals(ShellDestination.COLLECTIONS,nav.destination)
    }
    @Test fun settingsHistorySurvivesRecreationWithTheSameOrigin() {
        val nav=ShellNavigation();nav.open(ShellDestination.STORAGE);nav.open(ShellDestination.SETTINGS);nav.open(ShellDestination.ANNOTATIONS)
        val scope=object: SaverScope { override fun canBeSaved(value: Any)=true };val saved=with(ShellNavigation.Saver) { requireNotNull(scope.save(nav)) }
        val restored=requireNotNull(ShellNavigation.Saver.restore(saved));assertEquals(ShellDestination.ANNOTATIONS,restored.destination)
        restored.back();assertEquals(ShellDestination.SETTINGS,restored.destination);restored.back();assertEquals(ShellDestination.STORAGE,restored.destination)
    }
}
