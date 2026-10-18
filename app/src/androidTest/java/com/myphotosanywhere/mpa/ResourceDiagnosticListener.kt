package com.myphotosanywhere.mpa

import org.junit.runner.Description
import org.junit.runner.notification.RunListener

/** Recheck tracing at each case without perturbing gesture timing with forced collections. */
class ResourceDiagnosticListener : RunListener() {
    override fun testStarted(description: Description) {
        ResourceDiagnosticRunner.enable("test ${description.className}.${description.methodName}")
    }
}
