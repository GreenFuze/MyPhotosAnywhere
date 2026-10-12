package com.myphotosanywhere.mpa.data

import android.app.Application
import com.myphotosanywhere.mpa.MpaApplication
import com.myphotosanywhere.mpa.importing.ImportTransferIds
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory

@RunWith(RobolectricTestRunner::class)
@Config(sdk=[28],application=Application::class)
class WarningContractsTest {
    @Test fun backupAndDeviceTransferExcludeAllPrivateDataDomains() {
        val document=DocumentBuilderFactory.newInstance().newDocumentBuilder()
            .parse(File("src/main/res/xml/data_extraction_rules.xml"))
        val application=DocumentBuilderFactory.newInstance().newDocumentBuilder()
            .parse(File("src/main/AndroidManifest.xml")).getElementsByTagName("application").item(0)
        assertEquals("false",application.attributes.getNamedItem("android:allowBackup").nodeValue)
        assertEquals("false",application.attributes.getNamedItem("android:fullBackupContent").nodeValue)
        val domains=setOf("root","file","database","sharedpref","external",
            "device_root","device_file","device_database","device_sharedpref")
        for(section in listOf("cloud-backup","device-transfer")) {
            val exclusions=document.getElementsByTagName(section).item(0).childNodes
            val excluded=(0 until exclusions.length).map { exclusions.item(it) }
                .filter { it.nodeName=="exclude" }.associate {
                    it.attributes.getNamedItem("domain").nodeValue to it.attributes.getNamedItem("path").nodeValue
                }
            assertEquals(domains,excluded.keys)
            assertTrue(excluded.values.all { it=="." })
        }
    }
    @Test fun workManagerAndDurableImportIdsCannotCollide() {
        val application=org.robolectric.RuntimeEnvironment.getApplication()
        val config=MpaApplication().workManagerConfiguration
        val id=ImportTransferIds(application).id("warning-contract")
        assertTrue(config.minJobSchedulerId>0)
        assertTrue(config.maxJobSchedulerId<1_000_000)
        assertTrue(id>config.maxJobSchedulerId)
        assertEquals(id,ImportTransferIds(application).id("warning-contract"))
    }
}
