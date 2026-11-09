package com.myphotosanywhere.mpa

import android.app.Application
import android.content.Context
import android.os.Process
import android.os.StrictMode
import android.util.Log
import androidx.test.runner.AndroidJUnitRunner

/** Enable public-API allocation tracing before constructing the application under test.
 * Keep existing VM checks and all test assertions. Log violations through teardown so
 * their ownership can be investigated; no hidden-API exemption or production hook. */
class MpaDiagnosticTestRunner : AndroidJUnitRunner() {
    override fun newApplication(cl: ClassLoader, className: String, context: Context): Application {
        StrictMode.setVmPolicy(StrictMode.VmPolicy.Builder(StrictMode.getVmPolicy())
            .detectLeakedClosableObjects()
            .penaltyLog()
            .build())
        Log.i("MpaResourceDiagnostics", "Closable allocation tracing enabled before application construction; pid=${Process.myPid()}")
        return super.newApplication(cl, className, context)
    }
}
