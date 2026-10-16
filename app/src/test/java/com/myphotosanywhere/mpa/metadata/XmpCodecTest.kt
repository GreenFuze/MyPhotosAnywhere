package com.myphotosanywhere.mpa.metadata

import com.myphotosanywhere.mpa.people.FaceRegion
import com.myphotosanywhere.mpa.edit.EditRecipe
import org.junit.Assert.*
import org.junit.Test

class XmpCodecTest {
    private fun catalog()=XmpCatalog("account","folder",listOf(PortableAsset("asset","remote","a".repeat(64),1024,
        true,false,"Haifa & café <sea>",32.8,35.0,1234,null,"MPA","crop & rotate","Cat, Landscape",EditRecipe().json(),
        listOf("album"),listOf(PortableFace("face","person",true,FaceRegion(.1f,.2f,.4f,.6f),1600,1200,"model")))),
        listOf(PortablePerson("person","Sam & <Alex>")),listOf(PortableAlbum("album","Trip ☀")))
    @Test fun roundTripPreservesReviewedRegionsLocationsRecipesAndUnicode() {
        val original=catalog(); val xml=XmpCodec.encode(original)
        assertEquals(original,XmpCodec.decode(xml))
        assertFalse(xml.contains("embedding")); assertFalse(xml.contains("cropPath")); assertFalse(xml.contains("Authorization"))
    }
    @Test fun updatingOurCatalogPreservesExternalNamespacedAnnotations() {
        val original=XmpCodec.encode(catalog()).replace("</rdf:Description>","<other:Rating xmlns:other=\"urn:other\">5</other:Rating></rdf:Description>")
        val next=XmpCodec.encode(catalog().copy(people=listOf(PortablePerson("person","New name"))),original)
        assertTrue(next.contains("urn:other")); assertTrue(next.contains(">5</other:Rating>"))
        assertEquals("New name",XmpCodec.decode(next).people.single().name)
    }
    @Test fun documentsWithExternalEntitiesAreRejectedBeforeParsing() {
        val xml="<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///private'>]>"+XmpCodec.encode(catalog())
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.decode(xml) }
    }
    @Test fun duplicateAssetIdsAndDanglingIdentitiesCannotBeImported() {
        val c=catalog()
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(c.copy(assets=c.assets+c.assets)) }
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(c.copy(people=emptyList())) }
    }
    @Test fun invalidRegionsRecipesAndOversizedPayloadsAreRejected() {
        val xml=XmpCodec.encode(catalog())
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.decode(xml.replace("left=\"0.1\"","left=\"NaN\"")) }
        assertThrows(Exception::class.java) { XmpCodec.encode(catalog().copy(assets=catalog().assets.map { it.copy(recipe="{\"left\":2}") })) }
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.decode(" ".repeat(XmpCodec.MAX_BYTES+1)) }
    }
    @Test fun markupRecipeProvenanceIsNotLimitedToTheShortAiPromptLength() {
        val recipe=EditRecipe(strokes=listOf(com.myphotosanywhere.mpa.edit.Stroke(
            List(1200) { com.myphotosanywhere.mpa.edit.MarkPoint(.25f,.75f) }))).json()
        assertTrue(recipe.length>10000)
        val original=catalog().let { it.copy(assets=it.assets.map { a -> a.copy(instructions=recipe,recipe=recipe) }) }
        assertEquals(original,XmpCodec.decode(XmpCodec.encode(original)))
    }
    @Test fun photoLevelNamesRenamesExclusionsAndIgnoredAliasesRoundTripWithoutFaceGeometry() {
        val label=com.myphotosanywhere.mpa.people.ImportedPersonLabel("Sam","original-source")
        val ignored=com.myphotosanywhere.mpa.people.ImportedPersonLabel("Removed","original-source")
        val original=catalog().let { c -> c.copy(assets=c.assets.map { it.copy(faces=emptyList(),
            photoPeople=PortablePhotoPeople("original-source",listOf(PortableMembership(label.key,"person",true)))) },
            people=listOf(PortablePerson("person","Samuel")),aliases=listOf(
                PortableAlias(label.key,label.name,label.binding,"person",false),
                PortableAlias(ignored.key,ignored.name,ignored.binding,null,true))) }
        assertEquals(original,XmpCodec.decode(XmpCodec.encode(original)))
        assertTrue(XmpCodec.decode(XmpCodec.encode(original)).assets.single().faces.isEmpty())
    }
    @Test fun legacyCatalogDoesNotInventPhotoMembershipsOrAliases() {
        val restored=XmpCodec.decode(XmpCodec.encode(catalog()))
        assertNull(restored.assets.single().photoPeople);assertTrue(restored.aliases.isEmpty())
    }
    @Test fun aliasIdentityCollisionsDanglingDestinationsAndIgnoredMembersAreRejected() {
        val label=com.myphotosanywhere.mpa.people.ImportedPersonLabel("Sam",null)
        val valid=PortableAlias(label.key,label.name,null,"person",false)
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(catalog().copy(aliases=listOf(valid,valid))) }
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(catalog().copy(aliases=listOf(valid.copy(name="Another")))) }
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(catalog().copy(aliases=listOf(valid.copy(personId="missing")))) }
        val c=catalog().let { it.copy(assets=it.assets.map { a -> a.copy(photoPeople=PortablePhotoPeople(null,listOf(PortableMembership(label.key,"person",false)))) },
            aliases=listOf(valid.copy(personId=null,ignored=true))) }
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(c) }
    }
    @Test fun exportedNamesSupportTakeoutMaximumWithoutRelaxingDocumentBudget() {
        val c=catalog().copy(people=listOf(PortablePerson("person","א".repeat(200))))
        assertEquals(c,XmpCodec.decode(XmpCodec.encode(c)))
        assertThrows(IllegalArgumentException::class.java) { XmpCodec.encode(c.copy(people=listOf(PortablePerson("person","a".repeat(201))))) }
    }

}
