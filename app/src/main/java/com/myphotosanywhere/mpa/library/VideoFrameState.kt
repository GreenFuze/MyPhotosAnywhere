package com.myphotosanywhere.mpa.library

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/** Native video owns only its fitted rectangle; Compose owns the surrounding canvas. */
internal class VideoFrameState(width: Int,height: Int) {
    var aspect by mutableFloatStateOf(if(width>0 && height>0) width.toFloat()/height else 1f)
        private set
    var prepared by mutableStateOf(false)
        private set

    fun onPrepared(width: Int,height: Int) {
        if(width>0 && height>0) {
            aspect=width.toFloat()/height
            prepared=true
        }
    }
}
