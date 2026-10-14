package com.myphotosanywhere.mpa.drive

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class DriveAccessSessionTest {
    @Test fun concurrentRejectionsRefreshOnlyTheRejectedGeneration() = runBlocking {
        val entered=CompletableDeferred<Unit>(); val proceed=CompletableDeferred<Unit>(); var calls=0
        val session=DriveAccessSession("old") { rejected ->
            assertEquals("old",rejected);calls++;entered.complete(Unit);proceed.await();"new"
        }
        val first=async { session.rejected("old") };entered.await()
        val second=async { session.rejected("old") };proceed.complete(Unit)
        assertEquals("new",first.await());assertEquals("new",second.await());assertEquals(1,calls)
        assertEquals("new",session.current());assertEquals("new",session.rejected("old"));assertEquals(1,calls)
    }
    @Test fun invalidReplacementFailsWithoutReplacingTheLastToken() = runBlocking {
        val session=DriveAccessSession("old") { " " }
        try { session.rejected("old");fail("Blank token must fail") } catch(_: IllegalArgumentException) { }
        assertEquals("old",session.current())
        try { DriveAccessSession(" ",null);fail("Initial token must exist") } catch(_: IllegalArgumentException) { }
    }
    @Test fun aFailedRefreshPreservesTheOriginalFailureAndDoesNotMarkAccessRenewed() = runBlocking {
        val expected=DriveAuthorizationNeeded();val session=DriveAccessSession("old") { throw expected }
        try { session.rejected("old");fail("Consent required") } catch(error: DriveAuthorizationNeeded) { assertSame(expected,error) }
        assertEquals("old",session.current())
    }
}
