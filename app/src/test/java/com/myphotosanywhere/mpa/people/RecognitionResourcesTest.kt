package com.myphotosanywhere.mpa.people

import com.google.android.gms.tasks.TaskCompletionSource
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class RecognitionResourcesTest {
    @Test fun partialInitializationClosesEarlierResourcesInReverseOrder() {
        val closed=mutableListOf<String>()
        val failure=IllegalStateException("model initialization")
        val actual=assertThrows(IllegalStateException::class.java) {
            RecognitionResources().use { owner ->
                owner.acquire { AutoCloseable { closed+="detector" } }
                owner.acquire { AutoCloseable { closed+="labeler" } }
                owner.acquire<AutoCloseable> { throw failure }
            }
        }
        assertSame(failure,actual);assertEquals(listOf("labeler","detector"),closed)
    }

    @Test fun allClientsCloseEvenWhenOneCleanupThrowsAndPrimaryFailureIsPreserved() {
        val closed=mutableListOf<String>();val body=IllegalStateException("scan invariant")
        val model=IllegalArgumentException("model close");val detector=IllegalArgumentException("detector close")
        val actual=assertThrows(IllegalStateException::class.java) {
            RecognitionResources().use { owner ->
                owner.acquire { AutoCloseable { closed+="detector";throw detector } }
                owner.acquire { AutoCloseable { closed+="labeler" } }
                owner.acquire { AutoCloseable { closed+="model";throw model } }
                throw body
            }
        }
        assertSame(body,actual);assertArrayEquals(arrayOf(model),actual.suppressed)
        assertArrayEquals(arrayOf(detector),model.suppressed);assertEquals(listOf("model","labeler","detector"),closed)
    }

    @Test fun closedOwnerCannotAcquireAndRepeatedCloseDoesNotDoubleRelease() {
        var closes=0;var factoryRan=false;val owner=RecognitionResources()
        owner.acquire { AutoCloseable { closes++ } };owner.close();owner.close()
        assertEquals(1,closes)
        assertThrows(IllegalStateException::class.java) { owner.acquire { factoryRan=true;AutoCloseable {} } }
        assertFalse(factoryRan)
    }

    @Test fun cancelledScanRetainsNativeInputUntilTaskCompletesThenCancelsWithoutCommitting()=runTest {
        val native=TaskCompletionSource<Int>();var recycled=false;var committed=false;var clientClosed=false
        val scan=launch {
            RecognitionResources().use { owner ->
                owner.acquire { AutoCloseable { clientClosed=true } }
                try { native.task.awaitOwnedInput();committed=true } finally { recycled=true }
            }
        }
        runCurrent();scan.cancel();runCurrent()
        assertFalse(recycled);assertFalse(clientClosed);assertFalse(scan.isCompleted)
        native.setResult(1);runCurrent();scan.join()
        assertTrue(recycled);assertTrue(clientClosed);assertFalse(committed);assertTrue(scan.isCancelled)
    }

    @Test fun nativeFailurePropagatesAndReleasesOwnedInputAndClient()=runTest {
        val native=TaskCompletionSource<Int>();val failure=IllegalArgumentException("native failure")
        var recycled=false;var closed=false;var caught: Throwable?=null
        val scan=launch {
            try { RecognitionResources().use { owner ->
                owner.acquire { AutoCloseable { closed=true } }
                try { native.task.awaitOwnedInput() } finally { recycled=true }
            } } catch(error: IllegalArgumentException) { caught=error }
        }
        runCurrent();native.setException(failure);runCurrent();scan.join()
        val recovered=requireNotNull(caught)
        // Coroutine stack recovery can copy the exception; its original cause must survive.
        assertEquals(failure.javaClass,recovered.javaClass);assertEquals(failure.message,recovered.message)
        assertSame(failure,generateSequence(recovered) { it.cause }.last())
        assertTrue(recycled);assertTrue(closed)
    }
}
