package com.myphotosanywhere.mpa.core

import android.content.SharedPreferences
import java.lang.reflect.Proxy
import org.junit.Assert.*
import org.junit.Test

class CheckedPreferencesTest {
    @Test fun successfulKtxTransactionCommitsOnceWithoutApply() {
        val fake=Preferences(true)
        CheckedPreferences(fake.preferences).commit("failed") {
            putInt("job",42)
            putInt("next",43)
        }
        assertEquals(listOf("putInt","putInt","commit"),fake.calls)
    }
    @Test fun failedCommitPropagatesBeforeCallerCanReportSuccess() {
        val fake=Preferences(false)
        var continued=false
        try {
            CheckedPreferences(fake.preferences).commit("Cannot save job identity") { putInt("job",42) }
            continued=true
            fail("Failed persistence must stop the operation")
        } catch(expected: IllegalStateException) {
            assertEquals("Cannot save job identity",expected.message)
        }
        assertFalse(continued)
        assertEquals(listOf("putInt","commit"),fake.calls)
    }
    private class Preferences(private val success: Boolean) {
        val calls=mutableListOf<String>()
        private val editor=Proxy.newProxyInstance(SharedPreferences.Editor::class.java.classLoader,
            arrayOf(SharedPreferences.Editor::class.java)) { proxy,method,_ ->
            calls.add(method.name)
            when(method.name) {
                "commit" -> success
                "apply" -> error("A durable transaction cannot use apply")
                else -> proxy
            }
        } as SharedPreferences.Editor
        val preferences=Proxy.newProxyInstance(SharedPreferences::class.java.classLoader,
            arrayOf(SharedPreferences::class.java)) { _,method,_ ->
            check(method.name=="edit")
            editor
        } as SharedPreferences
    }
}
