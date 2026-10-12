package com.myphotosanywhere.mpa

import android.app.job.JobScheduler
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import com.myphotosanywhere.mpa.data.StorageBudget
import com.myphotosanywhere.mpa.importing.ImportTransferIds
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeUnit

/** Exercises real platform storage and WorkManager initialization after toolchain cleanup. */
@RunWith(AndroidJUnit4::class)
class WarningContractsDeviceTest {
    private val app get()=InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as MpaApplication

    @Test fun privateStorageBudgetUsesMountedVolumeWithoutTouchingOriginals()=runBlocking(Dispatchers.IO) {
        val original=java.io.File(app.filesDir,"storage-budget-original-fixture").apply { writeBytes(byteArrayOf(9,8,7)) }
        try {
            val budget=StorageBudget(app)
            budget.requireBytes(app.filesDir,16L*1024*1024,"Insufficient fixture capacity")
            assertTrue(budget.available(app.filesDir)>16L*1024*1024)
            assertArrayEquals(byteArrayOf(9,8,7),original.readBytes())
        } finally { original.delete() }
    }

    @Test fun onDemandWorkManagerSchedulesOutsideDurableImportIdRange() {
        val manager=WorkManager.getInstance(app)
        val name="warning-contract-scheduling"
        val work=OneTimeWorkRequestBuilder<com.myphotosanywhere.mpa.drive.CloudSyncWorker>()
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.UNMETERED).build()).build()
        try {
            manager.enqueueUniqueWork(name,ExistingWorkPolicy.REPLACE,work).result.get(15,TimeUnit.SECONDS)
            val scheduler=app.getSystemService(JobScheduler::class.java)
            val deadline=android.os.SystemClock.elapsedRealtime()+15_000
            var jobs=scheduler.allPendingJobs.filter { it.service.className=="androidx.work.impl.background.systemjob.SystemJobService" }
            while(jobs.isEmpty() && android.os.SystemClock.elapsedRealtime()<deadline) {
                Thread.sleep(50)
                jobs=scheduler.allPendingJobs.filter { it.service.className=="androidx.work.impl.background.systemjob.SystemJobService" }
            }
            assertTrue("WorkManager must schedule a constrained native job",jobs.isNotEmpty())
            assertTrue(jobs.all { it.id in 1..999_999 })
            val durable=ImportTransferIds(app).id(name)
            assertTrue(durable>=1_000_000)
            assertTrue(jobs.none { it.id==durable })
        } finally { manager.cancelUniqueWork(name).result.get(15,TimeUnit.SECONDS) }
    }
}
