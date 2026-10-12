package com.myphotosanywhere.mpa.core

import android.content.SharedPreferences
import androidx.core.content.edit

/** KTX transactions with an explicit durable-write failure contract. */
internal class CheckedPreferences(private val preferences: SharedPreferences) {
    fun commit(message: String,action: SharedPreferences.Editor.()->Unit) {
        CommitCheckingPreferences(preferences,message).edit(commit=true,action=action)
    }

    private class CommitCheckingPreferences(
        private val preferences: SharedPreferences,
        private val message: String,
    ): SharedPreferences by preferences {
        override fun edit(): SharedPreferences.Editor {
            val editor=preferences.edit()
            return object: SharedPreferences.Editor by editor {
                override fun commit(): Boolean {
                    check(editor.commit()) { message }
                    return true
                }
            }
        }
    }
}
