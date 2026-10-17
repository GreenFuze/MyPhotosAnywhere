package com.myphotosanywhere.mpa

import android.os.Build
import android.os.SystemClock
import android.util.Log
import android.view.SurfaceControl
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** An explicitly deliberate test-only leak proves that the allocation tracer is active. */
@RunWith(AndroidJUnit4::class)
class ResourceDiagnosticTest {
    private fun abandonDiagnosticSentinel() {
        check(Build.VERSION.SDK_INT>=29)
        val control=SurfaceControl.Builder().setName("MPA test-only CloseGuard sentinel").build()
        Log.i("MPAResourceTrace","Deliberately abandoning diagnostic-only sentinel ${System.identityHashCode(control)}")
    }
    @Test fun allocationTracingRecordsTheDeliberateTestOnlySentinel() {
        abandonDiagnosticSentinel()
        val deadline=SystemClock.elapsedRealtime()+10000
        fun seen()=ResourceDiagnosticRunner.violations.any { error -> error.stackTrace.any { it.className==javaClass.name } }
        while(!seen() && SystemClock.elapsedRealtime()<deadline) {
            System.gc();System.runFinalization();Thread.sleep(100)
        }
        assertTrue("Diagnostic sentinel must produce an allocation trace before other warnings can be attributed",seen())
    }
}
