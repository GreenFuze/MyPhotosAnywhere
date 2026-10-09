package com.myphotosanywhere.mpa.drive

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DriveBindingTest {
    @Test fun destinationIdentityCannotCollideByConcatenatingAccountAndFolder() {
        assertNotEquals(DriveBinding("a", "bc").key, DriveBinding("ab", "c").key)
        assertNotEquals(DriveBinding("account-a", "folder").key, DriveBinding("account-b", "folder").key)
        assertNotEquals(DriveBinding("account", "folder-a").key, DriveBinding("account", "folder-b").key)
    }
    @Test fun switchingAccountOrFolderDoesNotAcceptOldTasks() {
        val binding = DriveBinding("account", "folder")
        assertTrue(binding.accepts("account", "folder"))
        assertFalse(binding.accepts("other-account", "folder"))
        assertFalse(binding.accepts("account", "other-folder"))
    }
    @Test fun uploadAcknowledgementsIncludeByteZeroAndHandleAnEmptySession() {
        assertEquals(0L, UploadRanges.nextOffset(null, 10))
        assertEquals(1L, UploadRanges.nextOffset("bytes=0-0", 10))
        assertEquals(10L, UploadRanges.nextOffset("bytes=0-9", 10))
    }
    @Test(expected = IllegalArgumentException::class)
    fun rejectsServerOffsetsPastEndOfOriginal() { UploadRanges.nextOffset("bytes=0-10", 10) }
    @Test(expected = IllegalStateException::class)
    fun rejectsMalformedAcknowledgements() { UploadRanges.nextOffset("bytes=5-9", 10) }
}
