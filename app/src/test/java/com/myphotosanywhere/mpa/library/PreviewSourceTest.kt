package com.myphotosanywhere.mpa.library

import org.junit.Assert.*
import org.junit.Test
import java.io.File

class PreviewSourceTest {
    @Test fun providerReferencesNeverGoDirectlyToAnUncheckedImageLoader() {
        assertNull(previewSource("content://media/external/images/media/42"))
    }
    @Test fun retainedPreviewBytesRemainAvailableWithoutProviderAccess() {
        assertEquals(File("/owned/previews/photo.jpg"),previewSource("/owned/previews/photo.jpg"))
    }
    @Test fun absentPreviewDoesNotBecomeARelativeFilesystemPath() {
        assertNull(previewSource(""))
    }
}
