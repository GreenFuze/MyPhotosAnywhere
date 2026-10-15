package com.myphotosanywhere.mpa.ui

/** Presentation policy: forms, selection and assisted navigation keep their actions pinned. */
internal object GalleryBarPolicy {
    fun canCollapse(destination: Int,selecting: Boolean,touchExploration: Boolean,keyboard: Boolean)=
        destination==ShellDestination.PHOTOS.id && !selecting && !touchExploration && !keyboard
}
