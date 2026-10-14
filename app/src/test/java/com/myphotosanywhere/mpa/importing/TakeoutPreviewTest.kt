package com.myphotosanywhere.mpa.importing

import kotlinx.coroutines.CancellationException
import org.junit.Assert.*
import org.junit.Test

class TakeoutPreviewTest {
    private fun item(id: String,raw: String?=null,selected: Boolean=true)=ImportItem(id,"job","content://source","album/$id.jpg","$id.jpg","image/jpeg",12,metadata=raw,selected=selected)
    private fun raw(names: String="")="""{"title":"photo.jpg","photoTakenTime":{"timestamp":"123"},"people":[$names]}"""
    private fun snapshot(entries: List<ImportItem>)=TakeoutPreviewSnapshot(entries,TakeoutPreviewAnalyzer().analyze(TakeoutPreviewSnapshot.inputs(entries)))

    @Test fun distinguishesExportedLabelsFromMissingAndUnmatchedMetadata() {
        val named=item("named",raw("""{"name":" Sam "},{"name":"Sam"},{"name":""},{"name":"דנה"}"""))
        val entries=listOf(named,item("plain",raw()),item("missing"),item("invalid","{"),
            item("json").copy(mime="application/json",state="UNRESOLVED",metadata=raw(),selected=false,error="Missing media"))
        val result=snapshot(entries)
        assertEquals(4,result.mediaCount);assertEquals(2,result.withMetadata);assertEquals(1,result.withoutMetadata)
        assertEquals(1,result.invalidMetadata);assertEquals(1,result.withPeople);assertEquals(2,result.differentNames)
        assertEquals(4,result.selectedMedia);assertEquals(1,result.selectedWithPeople);assertEquals(2,result.reviewCount)
        assertEquals(listOf("named"),result.visible(TakeoutPreviewFilter.PEOPLE).map { it.id })
        assertEquals(listOf("missing"),result.visible(TakeoutPreviewFilter.MISSING).map { it.id })
        assertEquals(listOf("invalid","json"),result.visible(TakeoutPreviewFilter.REVIEW).map { it.id })
        assertEquals(entries,result.visible(TakeoutPreviewFilter.ALL))
    }
    @Test fun filteringNeverChangesSelectionOrJobTransferState() {
        val entries=listOf(item("a",raw("""{"name":"Sam"}""")).copy(state="UPLOADING",offset=42,remoteId="remote",sessionUri="session"),item("b",selected=false))
        val before=entries.toList();val result=snapshot(entries)
        TakeoutPreviewFilter.entries.forEach { result.visible(it) }
        assertEquals(before,entries);assertEquals(42L,result.visible(TakeoutPreviewFilter.PEOPLE).single().offset)
        assertEquals(1,result.selectedMedia);assertEquals(1,result.selectedWithPeople)
    }
    @Test fun selectionUpdatesReuseMetadataAndKeepUnselectedLabelsDiscoverable() {
        val entries=listOf(item("a",raw("""{"name":"Sam"}""")),item("b"))
        val metadata=TakeoutPreviewAnalyzer().analyze(TakeoutPreviewSnapshot.inputs(entries))
        val result=TakeoutPreviewSnapshot(entries.map { it.copy(selected=false,state="PAUSED",offset=20) },metadata)
        assertEquals(0,result.selectedWithPeople);assertEquals(1,result.withPeople)
        assertEquals(1,result.visible(TakeoutPreviewFilter.PEOPLE).size)
    }
    @Test fun staleMetadataOrDuplicateIdentitiesFailFast() {
        val a=item("a",raw());val metadata=TakeoutPreviewAnalyzer().analyze(TakeoutPreviewSnapshot.inputs(listOf(a)))
        assertThrows(IllegalArgumentException::class.java) { TakeoutPreviewSnapshot(listOf(a.copy(metadata=raw("""{"name":"Sam"}"""))),metadata) }
        assertThrows(IllegalArgumentException::class.java) { snapshot(listOf(a,a)) }
    }
    @Test fun malformedExternalRecordsRemainExplicitRatherThanPretendingNamesAreAbsent() {
        val invalid=listOf("{",raw().replace("123","bad"),raw().replace("[]","[null]"),raw().replace("[]","{}"),raw().replace("123",Long.MAX_VALUE.toString()))
        val result=snapshot(invalid.mapIndexed { index,text -> item("$index",text) })
        assertEquals(invalid.size,result.invalidMetadata);assertEquals(0,result.withMetadata)
        assertEquals(invalid.size,result.visible(TakeoutPreviewFilter.REVIEW).size)
    }
    @Test fun cancellationAndUnexpectedFailuresPropagate() {
        val inputs=TakeoutPreviewSnapshot.inputs(listOf(item("a",raw())))
        assertThrows(CancellationException::class.java) { TakeoutPreviewAnalyzer().analyze(inputs) { throw CancellationException("cancelled") } }
        assertThrows(AssertionError::class.java) { TakeoutPreviewAnalyzer().analyze(inputs) { throw AssertionError("invariant") } }
    }
    @Test fun currentSupplementalNamesMatchWithinTheirDirectoryIncludingNumberedMedia() {
        val codec=TakeoutCodec();val parsed=codec.parse(raw("""{"name":"Sam"}"""))
        assertEquals(listOf("album/photo.jpg"),codec.candidates("album/photo.jpg.supplemental-metadata.json",parsed,listOf("album/photo.jpg","other/photo.jpg")))
        assertEquals(listOf("album/photo(0).jpg"),codec.candidates("album/photo(0).jpg.supplemental-metadata.json",parsed,listOf("album/photo.jpg","album/photo(0).jpg")))
        assertTrue(codec.candidates("album/photo.jpg.supplemental-metadata.json",parsed,listOf("other/photo.jpg")).isEmpty())
    }
    @Test fun emptyExportIsReadyAndDoesNotInventPeople() {
        val result=snapshot(emptyList());assertEquals(0,result.mediaCount);assertEquals(0,result.withPeople)
        TakeoutPreviewFilter.entries.forEach { assertTrue(result.visible(it).isEmpty()) }
    }
}
