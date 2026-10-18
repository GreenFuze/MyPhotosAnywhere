package com.myphotosanywhere.mpa

import android.os.SystemClock
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.io.FileInputStream
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** An explicitly deliberate test-only leak proves that the allocation tracer is active. */
@RunWith(AndroidJUnit4::class)
class ResourceDiagnosticTest {
    private fun abandonDiagnosticSentinel(file: File) {
        val stream=FileInputStream(file)
        check(stream.fd.valid())
        Log.i("MPAResourceTrace","Deliberately abandoning diagnostic-only sentinel ${System.identityHashCode(stream)}")
    }
    @Test fun allocationTracingRecordsTheDeliberateTestOnlySentinel() {
        val file=File.createTempFile("resource-trace-sentinel-",".txt",InstrumentationRegistry.getInstrumentation().targetContext.cacheDir)
        try {
            file.writeText("Explicitly deliberate test-only allocation sentinel")
            abandonDiagnosticSentinel(file)
            val deadline=SystemClock.elapsedRealtime()+10000
            fun seen()=ResourceDiagnosticRunner.violations.any { error ->
                generateSequence(error) { it.cause }.any { cause -> cause.stackTrace.any { it.className==javaClass.name } }
            }
            while(!seen() && SystemClock.elapsedRealtime()<deadline) {
                System.gc();System.runFinalization();Thread.sleep(100)
            }
            assertTrue("Diagnostic sentinel must produce an allocation trace before other warnings can be attributed",seen())
        } finally {
            check(file.delete())
        }
    }
}
