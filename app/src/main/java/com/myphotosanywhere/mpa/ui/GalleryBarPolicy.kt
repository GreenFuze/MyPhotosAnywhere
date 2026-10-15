package com.myphotosanywhere.mpa.ui

/** Presentation policy: forms, selection and assisted navigation keep their actions pinned. */
internal object GalleryBarPolicy {
    fun navigationKey(code: Int)=code in setOf(android.view.KeyEvent.KEYCODE_TAB,android.view.KeyEvent.KEYCODE_ENTER,
        android.view.KeyEvent.KEYCODE_NUMPAD_ENTER,android.view.KeyEvent.KEYCODE_SPACE,android.view.KeyEvent.KEYCODE_DPAD_UP,
        android.view.KeyEvent.KEYCODE_DPAD_DOWN,android.view.KeyEvent.KEYCODE_DPAD_LEFT,android.view.KeyEvent.KEYCODE_DPAD_RIGHT,
        android.view.KeyEvent.KEYCODE_DPAD_CENTER)
    fun canCollapse(destination: Int,selecting: Boolean,touchExploration: Boolean,keyboard: Boolean)=
        destination==ShellDestination.PHOTOS.id && !selecting && !touchExploration && !keyboard
}
