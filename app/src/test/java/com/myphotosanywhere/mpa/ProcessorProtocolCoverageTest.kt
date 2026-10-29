package com.myphotosanywhere.mpa

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.myphotosanywhere.mpa.processor.protocol.CanonicalProtocolJson
import com.myphotosanywhere.mpa.processor.protocol.CatalogCoveragePage
import com.myphotosanywhere.mpa.processor.protocol.CatalogPublicationReader
import com.myphotosanywhere.mpa.processor.protocol.CatalogSourceCoverageSet
import com.myphotosanywhere.mpa.processor.protocol.CatalogWorkspace
import com.myphotosanywhere.mpa.processor.protocol.DriveCatalogAsset
import com.myphotosanywhere.mpa.processor.protocol.DriveCatalogSource
import com.myphotosanywhere.mpa.processor.protocol.ProcessorProtocolException
import com.myphotosanywhere.mpa.processor.protocol.ProtocolCompatibilityException
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ProcessorProtocolCoverageTest {
    private val library=UUID.fromString("94a81164-e980-4ec1-a566-cec36475b839")
    private val workspace=CatalogWorkspace("workspace-account","workspace-folder")
    private fun fixture(): JsonObject=JsonParser.parseString(requireNotNull(javaClass.getResourceAsStream("/v1/catalog-coverage.json"))
        .bufferedReader(Charsets.UTF_8).use { it.readText() }).asJsonObject
    private fun bytes(value: JsonElement)=CanonicalProtocolJson(CatalogPublicationReader.MAX_INDEX_BYTES).encode(value.toString().toByteArray(Charsets.UTF_8))
    private fun read(graph: JsonObject,onAsset: (DriveCatalogAsset)->Unit={}): CatalogSourceCoverageSet {
        val reader=CatalogPublicationReader(workspace);val root=reader.readCoverageRoot(bytes(graph["root"]),library)
        val rows=graph["pages"].asJsonArray;val pages=mutableListOf<CatalogCoveragePage>()
        val indices=graph["consumePages"]?.asJsonArray?.map { it.asInt } ?: (0 until rows.size()).toList()
        for(index in indices) pages.add(reader.readCoveragePage(bytes(rows[index]),root,root.pages[index]))
        val coverage=CatalogSourceCoverageSet(root,pages)
        for(row in graph["assets"].asJsonArray) {
            val value=row.asJsonObject
            val memberships=value["sources"].asJsonArray.map {
                val member=it.asJsonObject
                DriveCatalogSource(UUID.fromString(member["id"].asString),member["revision"].asString.toLong(),member["depth"].asInt)
            }
            val asset=DriveCatalogAsset(UUID.randomUUID(),value["accountId"].asString,"synthetic-file",1,"photo.jpg","image/jpeg",
                null,null,null,null,null,memberships)
            coverage.validateAsset(asset);onAsset(asset)
        }
        return coverage
    }
    @Test fun boundedCoveragePreservesPartialDisconnectedEmptyAndMaximumLibraries() {
        val fixture=fixture();var accepted=0
        val coverage=read(fixture["valid"].asJsonObject) { accepted++ }
        assertEquals(3,accepted);assertEquals(3,coverage.root.sourceCount)
        assertEquals(7L,coverage.root.configurationRevision);assertEquals(31L,coverage.root.discoveryRevision)
        assertEquals(0,read(fixture["empty"].asJsonObject).root.sourceCount)
        assertEquals(8,read(fixture["states"].asJsonObject).root.sourceCount)
        assertEquals(256,read(fixture["maximum"].asJsonObject).root.sourceCount)
    }
    @Test fun bothConsumersRejectEveryIndependentMalformedCoverageAndMembershipCase() {
        val cases=fixture()["invalid"].asJsonArray;assertEquals(44,cases.size())
        for(item in cases) {
            val value=item.asJsonObject;val name=value["name"].asString
            if(name=="unsupported-major") assertThrows(name,ProtocolCompatibilityException::class.java) { read(value["graph"].asJsonObject) }
            else assertThrows(name,ProcessorProtocolException::class.java) { read(value["graph"].asJsonObject) }
        }
    }
    @Test fun coverageRequiresCanonicalBoundedInputAndAnEstablishedLibrary() {
        val reader=CatalogPublicationReader(workspace);val bytes=bytes(fixture()["valid"].asJsonObject["root"])
        assertThrows(ProcessorProtocolException::class.java) { reader.readCoverageRoot(bytes+32.toByte(),library) }
        assertThrows(ProcessorProtocolException::class.java) { reader.readCoverageRoot(ByteArray(CatalogPublicationReader.MAX_INDEX_BYTES+1),library) }
        assertThrows(IllegalArgumentException::class.java) { reader.readCoverageRoot(bytes,UUID(0,0)) }
    }
    @Test fun coveragePageCannotBeReadUsingAnotherSelectedWorkspace() {
        val graph=fixture()["valid"].asJsonObject;val reader=CatalogPublicationReader(workspace)
        val root=reader.readCoverageRoot(bytes(graph["root"]),library)
        val foreign=CatalogPublicationReader(CatalogWorkspace("other-account","other-workspace"))
        assertThrows(ProcessorProtocolException::class.java) { foreign.readCoveragePage(bytes(graph["pages"].asJsonArray[0]),root,root.pages[0]) }
    }
}
