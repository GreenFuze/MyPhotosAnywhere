package com.myphotosanywhere.mpa.core

import org.junit.Assert.assertTrue
import org.junit.Test

class EditPromptTest {
    @Test fun includesInstructionsAndAPathForProvidersWithoutImageEditing() {
        val prompt = EditPrompt.build("  Remove the lamp.  ")
        assertTrue(prompt.contains("\n\nRemove the lamp.\n\n"))
        assertTrue(prompt.contains("Otherwise explain the steps"))
    }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsEmptyInstructions() { EditPrompt.build(" \n ") }

    @Test(expected = IllegalArgumentException::class)
    fun rejectsExcessiveInstructions() { EditPrompt.build("x".repeat(4001)) }
}
