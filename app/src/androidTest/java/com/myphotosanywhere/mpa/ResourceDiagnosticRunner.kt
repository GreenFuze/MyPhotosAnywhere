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
    // ActivityThread constructs instrumentation before Application/content providers,
    // then invokes onCreate only after providers. Trace their startup allocations too.
    init { enable("runner construction before providers") }
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
    override fun finish(resultCode: Int, results: Bundle?) {
        // Emulator cleanup only retains bounded log buffers. Preserve every collected
        // allocation stack independently before instrumentation tears down its process.
        System.gc()
        System.runFinalization()
        val records=org.json.JSONArray()
        violations.forEach { error -> records.put(org.json.JSONObject()
            .put("type",error.javaClass.name).put("message",error.message)
            .put("stack",Log.getStackTraceString(error))) }
        val report=org.json.JSONObject().put("api",Build.VERSION.SDK_INT).put("resultCode",resultCode).put("traces",records)
        java.io.File(targetContext.cacheDir,"resource-allocation-traces.json").outputStream().use {
            it.write(report.toString(2).toByteArray(Charsets.UTF_8))
            it.fd.sync()
        }
        super.finish(resultCode,results)
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
