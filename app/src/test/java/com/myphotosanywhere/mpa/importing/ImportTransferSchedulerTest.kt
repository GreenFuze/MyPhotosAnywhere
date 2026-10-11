package com.myphotosanywhere.mpa.importing

import android.app.Application
import android.app.Notification
import android.net.NetworkCapabilities
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[34],application=Application::class)
class ImportTransferSchedulerTest {
    private val context get()=RuntimeEnvironment.getApplication()
    @Test fun durableIdsSurviveRecreationAndHashCollisions() {
        assertEquals("Aa".hashCode(),"BB".hashCode())
        val first=ImportTransferIds(context).id("Aa"); val second=ImportTransferIds(context).id("BB")
        assertNotEquals(first,second); assertEquals(first,ImportTransferIds(context).id("Aa"))
    }
    @Test fun userInitiatedJobKeepsIdentityNetworkPreferenceAndRemainingByteEstimate() {
        val items=listOf(ImportItem("item","job","content://zip","a","a","image/jpeg",100,offset=40),
            ImportItem("done","job","content://zip","b","b","image/jpeg",500,state="COMPLETE"))
        val info=ImportTransferScheduler.info(context,123,"job",true,items)
        assertTrue(info.isUserInitiated); assertEquals("job",info.extras.getString("job"))
        assertEquals(60L,info.estimatedNetworkUploadBytes)
        assertTrue(requireNotNull(info.requiredNetwork).hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
        val metered=ImportTransferScheduler.info(context,124,"job",false,items)
        assertFalse(requireNotNull(metered.requiredNetwork).hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED))
    }
    @Test fun activeRetryNotificationRetainsBarAndGracefulPauseTargetsOnlyItsJob() {
        val progress=ImportProgress("TAKEOUT","RUNNING",14,1647,"Retrying automatically (1/3).")
        val notice=ImportNotifications(context).notification(progress,"job")
        assertTrue(notice.flags and Notification.FLAG_ONGOING_EVENT!=0)
        assertEquals(14,notice.extras.getInt(Notification.EXTRA_PROGRESS))
        assertEquals(1647,notice.extras.getInt(Notification.EXTRA_PROGRESS_MAX))
        assertEquals("Pause import",notice.actions.single().title)
        assertEquals("job",shadowOf(notice.actions.single().actionIntent).savedIntent.getStringExtra("job"))
        assertTrue(notice.actions.single().actionIntent.isImmutable)
    }
}
