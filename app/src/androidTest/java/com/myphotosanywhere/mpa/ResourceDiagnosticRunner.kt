package com.myphotosanywhere.mpa

import android.os.Bundle
import android.os.StrictMode
import android.os.Build
import android.app.Application
import android.util.Log
import androidx.test.runner.AndroidJUnitRunner
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executor

/** Test-only allocation traces; the production application and warning policy are unchanged. */
class ResourceDiagnosticRunner : AndroidJUnitRunner() {
    override fun onCreate(arguments: Bundle?) {
        val diagnosticArguments=arguments ?: Bundle()
        diagnosticArguments.putString("listener",listOfNotNull(diagnosticArguments.getString("listener"),ResourceDiagnosticListener::class.java.name).joinToString(","))
        enable("runner create")
        super.onCreate(diagnosticArguments)
    }
    override fun callApplicationOnCreate(app: Application) {
        super.callApplicationOnCreate(app)
        enable("application created")
    }
    override fun onStart() {
        enable("runner start")
        super.onStart()
    }
    companion object {
        val violations=ConcurrentLinkedQueue<Throwable>()
        fun enable(stage: String) {
            val builder=StrictMode.VmPolicy.Builder(StrictMode.getVmPolicy()).detectLeakedClosableObjects().penaltyLog()
            if(Build.VERSION.SDK_INT>=28) builder.penaltyListener(Executor { it.run() }) {
                violations.add(it)
                Log.i("MPAResourceTrace","Tracked VM allocation violation",it)
            }
            val policy=builder.build();StrictMode.setVmPolicy(policy)
            Log.i("MPAResourceTrace","Allocation tracing active at $stage: ${StrictMode.getVmPolicy()===policy}")
        }
    }
}
