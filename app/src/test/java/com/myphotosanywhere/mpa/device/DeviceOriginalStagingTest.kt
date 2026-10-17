package com.myphotosanywhere.mpa.device

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class DeviceOriginalStagingTest {
    @Test fun recoveryRemovesOnlyOwnedInterruptedStagesAndPreservesOtherFiles() {
        val root=Files.createTempDirectory("mpa-device-staging-test").toFile()
        try {
            val stage=root.resolve("mpa-device-interrupted.part").apply { writeText("partial") }
            val unrelated=root.resolve("original.jpg").apply { writeText("original") }
            val owner=DeviceOriginalStaging(root);val created=owner.create()
            assertFalse(stage.exists());assertEquals("original",unrelated.readText());assertTrue(created.isFile)
            owner.remove(created);assertFalse(created.exists());assertTrue(unrelated.isFile)
        } finally { root.deleteRecursively() }
    }
    @Test fun cleanupRejectsPathsOutsideItsOwnedDirectory() {
        val root=Files.createTempDirectory("mpa-device-staging-boundary").toFile()
        try {
            val owner=DeviceOriginalStaging(root.resolve("staging"))
            val outside=root.resolve("mpa-device-original.part").apply { writeText("keep") }
            assertThrows(IllegalStateException::class.java) { owner.remove(outside) }
            assertEquals("keep",outside.readText())
        } finally { root.deleteRecursively() }
    }
}
