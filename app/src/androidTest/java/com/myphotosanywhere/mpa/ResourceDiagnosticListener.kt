package com.myphotosanywhere.mpa

import org.junit.runner.Description
import org.junit.runner.notification.RunListener

/** Allocation tracing only; finalization runs after each Activity test has disposed its resources. */
class ResourceDiagnosticListener : RunListener() {
    override fun testFinished(description: Description) {
        System.gc()
        System.runFinalization()
        Thread.sleep(50)
    }
}
