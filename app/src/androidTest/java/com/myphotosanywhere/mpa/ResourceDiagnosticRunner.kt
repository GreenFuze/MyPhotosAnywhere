package com.myphotosanywhere.mpa

import android.os.Bundle
import android.os.StrictMode
import androidx.test.runner.AndroidJUnitRunner

/** Test-only allocation traces; the production application and warning policy are unchanged. */
class ResourceDiagnosticRunner : AndroidJUnitRunner() {
    override fun onCreate(arguments: Bundle?) {
        StrictMode.setVmPolicy(StrictMode.VmPolicy.Builder(StrictMode.getVmPolicy())
            .detectLeakedClosableObjects().penaltyLog().build())
        super.onCreate(arguments)
    }
}
