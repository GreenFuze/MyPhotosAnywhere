package com.myphotosanywhere.mpa.importing

import android.app.Application
import android.app.Notification
import android.app.NotificationManager
import com.myphotosanywhere.mpa.R
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class ImportNotificationsTest {
    private val context get()=RuntimeEnvironment.getApplication()
    @Test fun foregroundUsesMpaIconSilentChannelAndVerifiedProgress() {
        val notices=ImportNotifications(context)
        val notice=notices.foreground(42,ImportProgress("TAKEOUT","RUNNING",2,5,"Uploading photo · 50%")).notification
        assertEquals(R.drawable.ic_import_notification,notice.smallIcon.resId)
        assertEquals(5,notice.extras.getInt(Notification.EXTRA_PROGRESS_MAX))
        assertEquals(2,notice.extras.getInt(Notification.EXTRA_PROGRESS))
        assertTrue(notice.flags and Notification.FLAG_ONGOING_EVENT != 0)
        assertEquals(Notification.VISIBILITY_PRIVATE,notice.visibility)
        assertEquals(NotificationManager.IMPORTANCE_LOW,context.getSystemService(NotificationManager::class.java)
            .getNotificationChannel(ImportNotifications.CHANNEL).importance)
        assertEquals(ImportNotifications.ACTION_OPEN,shadowOf(notice.contentIntent).savedIntent.action)
    }
    @Test fun deniedNotificationsDoNotPreventForegroundInfoOrImportAndSummaryIsSkipped() {
        val manager=context.getSystemService(NotificationManager::class.java)
        shadowOf(manager).setNotificationsEnabled(false)
        val notices=ImportNotifications(context)
        assertFalse(notices.enabled())
        assertNotNull(notices.foreground(42,ImportProgress("PICKER","RUNNING",0,1,"Preparing")))
        notices.finish("denied",ImportProgress("PICKER","COMPLETE",1,1,"Import complete"))
        assertEquals(0,manager.activeNotifications.size)
    }
}
