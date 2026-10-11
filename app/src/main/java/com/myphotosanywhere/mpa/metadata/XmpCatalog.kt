package com.myphotosanywhere.mpa.metadata

import com.myphotosanywhere.mpa.people.FaceRegion
import com.myphotosanywhere.mpa.edit.EditRecipe
import java.io.StringReader
import java.io.StringWriter
import javax.xml.parsers.DocumentBuilderFactory
import javax.xml.transform.TransformerFactory
import javax.xml.transform.dom.DOMSource
import javax.xml.transform.stream.StreamResult
import org.w3c.dom.Element
import org.xml.sax.InputSource

data class PortablePerson(val id: String,val name: String)
data class PortableAlbum(val id: String,val name: String)
data class PortableFace(val id: String,val personId: String?,val corrected: Boolean,val region: FaceRegion?,
    val width: Int?,val height: Int?,val model: String?)
data class PortableAsset(val id: String,val remoteId: String,val sha256: String,val bytes: Long,
    val favorite: Boolean,val archived: Boolean,val place: String,val latitude: Double?,val longitude: Double?,
    val capturedAt: Long?,val parentId: String?,val provider: String?,val instructions: String?,val labels: String,
    val recipe: String?,val albums: List<String>,val faces: List<PortableFace>,val importedMetadata: String = "")
data class XmpCatalog(val accountId: String,val folderId: String,val assets: List<PortableAsset>,
    val people: List<PortablePerson>,val albums: List<PortableAlbum>)

/** MPA RDF extension. Unknown namespaces are retained; embeddings, tokens and paths are never exported. */
object XmpCodec {
    const val NS="https://myphotosanywhere.app/ns/1.0/"
    private const val RDF="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
    const val MAX_BYTES=2*1024*1024
    private fun document(xml: String) = DocumentBuilderFactory.newInstance().apply {
        isNamespaceAware=true; isExpandEntityReferences=false
        // Android's parser does not implement every JAXP feature. Reject declarations before parsing too.
        require(!Regex("<!\\s*(DOCTYPE|ENTITY)",RegexOption.IGNORE_CASE).containsMatchIn(xml)) { "XMP document declarations are not supported." }
        require(xml.toByteArray(Charsets.UTF_8).size<=MAX_BYTES) { "XMP exceeds the 2 MiB limit." }
        runCatching { setFeature("http://apache.org/xml/features/disallow-doctype-decl",true) }
        runCatching { setFeature("http://xml.org/sax/features/external-general-entities",false) }
        runCatching { setFeature("http://xml.org/sax/features/external-parameter-entities",false) }
    }.newDocumentBuilder().parse(InputSource(StringReader(xml)))
    fun encode(catalog: XmpCatalog,previous: String?=null): String {
        val doc=document(previous ?: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"$RDF\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>")
        val descriptions=doc.getElementsByTagNameNS(RDF,"Description")
        require(descriptions.length>0) { "Missing XMP RDF description." }
        val old=doc.getElementsByTagNameNS(NS,"Catalog")
        // Only our catalog is replaced. Lightroom/MWG and other external fields are preserved.
        while(old.length>0) old.item(0).parentNode.removeChild(old.item(0))
        fun node(name: String,attrs: Map<String,Any?>): Element = doc.createElementNS(NS,"mpa:$name").apply {
            attrs.forEach { (key,value) -> if(value!=null) setAttribute(key,value.toString()) }
        }
        val root=node("Catalog",mapOf("schema" to 1,"account" to catalog.accountId,"folder" to catalog.folderId))
        root.setAttributeNS("http://www.w3.org/2000/xmlns/","xmlns:mpa",NS)
        descriptions.item(0).appendChild(root)
        catalog.people.forEach { root.appendChild(node("Person",mapOf("id" to it.id,"name" to it.name))) }
        catalog.albums.forEach { root.appendChild(node("Album",mapOf("id" to it.id,"name" to it.name))) }
        catalog.assets.forEach { a ->
            val asset=node("Asset",mapOf("id" to a.id,"remote" to a.remoteId,"sha256" to a.sha256,"bytes" to a.bytes,
                "favorite" to a.favorite,"archived" to a.archived,"place" to a.place,"latitude" to a.latitude,
                "longitude" to a.longitude,"captured" to a.capturedAt,"parent" to a.parentId,"provider" to a.provider,
                "instructions" to a.instructions,"labels" to a.labels,"recipe" to a.recipe,"importedMetadata" to a.importedMetadata))
            root.appendChild(asset)
            a.albums.forEach { asset.appendChild(node("Member",mapOf("album" to it))) }
            a.faces.forEach { f -> asset.appendChild(node("Face",mapOf("id" to f.id,"person" to f.personId,
                "corrected" to f.corrected,"left" to f.region?.left,"top" to f.region?.top,"right" to f.region?.right,
                "bottom" to f.region?.bottom,"width" to f.width,"height" to f.height,"model" to f.model))) }
        }
        val output=StringWriter()
        TransformerFactory.newInstance().newTransformer().transform(DOMSource(doc),StreamResult(output))
        return output.toString().also { decode(it) } // Apply the same limits to our own exports.
    }
    fun decode(xml: String): XmpCatalog {
        val doc=document(xml); val roots=doc.getElementsByTagNameNS(NS,"Catalog")
        require(roots.length==1) { "Expected one MPA XMP catalog." }
        val root=roots.item(0) as Element
        fun Element.text(key: String,max: Int=200): String? = getAttribute(key).takeIf { hasAttribute(key) }?.also { require(it.length<=max) }
        fun Element.required(key: String,max: Int=200) = requireNotNull(text(key,max)).also { require(it.isNotBlank()) }
        fun Element.flag(key: String): Boolean = when(required(key)) { "true" -> true; "false" -> false; else -> error("Invalid XMP boolean.") }
        fun Element.children(name: String) = (0 until childNodes.length).mapNotNull { childNodes.item(it) as? Element }.filter { it.namespaceURI==NS && it.localName==name }
        fun Element.number(key: String) = text(key)?.toDouble()?.also { require(it.isFinite()) }
        require(root.required("schema")=="1") { "Unsupported MPA metadata version." }
        val people=root.children("Person").map { PortablePerson(it.required("id"),it.required("name",100)) }
        val albums=root.children("Album").map { PortableAlbum(it.required("id"),it.required("name",100)) }
        val assets=root.children("Asset").map { a ->
            val lat=a.number("latitude"); val lon=a.number("longitude")
            require((lat==null)==(lon==null) && (lat==null || lat in -90.0..90.0 && lon!! in -180.0..180.0))
            val recipe=a.text("recipe",MAX_BYTES)?.also { EditRecipe.parse(it) }
            val faces=a.children("Face").map { f ->
                val l=f.number("left"); val t=f.number("top"); val r=f.number("right"); val b=f.number("bottom")
                require(listOf(l,t,r,b).all { it==null } || listOf(l,t,r,b).all { it!=null })
                val region=if(l==null) null else FaceRegion(l.toFloat(),t!!.toFloat(),r!!.toFloat(),b!!.toFloat())
                PortableFace(f.required("id"),f.text("person"),f.flag("corrected"),region,
                    f.text("width")?.toInt()?.also { require(it>0) },f.text("height")?.toInt()?.also { require(it>0) },f.text("model"))
            }
            val members=a.children("Member").map { it.required("album") }
            PortableAsset(a.required("id"),a.required("remote"),a.required("sha256").also { require(it.matches(Regex("[a-f0-9]{64}"))) },
                a.required("bytes").toLong().also { require(it>0) },a.flag("favorite"),a.flag("archived"),a.text("place",500) ?: "",
                lat,lon,a.text("captured")?.toLong(),a.text("parent"),a.text("provider"),a.text("instructions",MAX_BYTES),
                a.text("labels",5000) ?: "",recipe,members,faces,(a.text("importedMetadata",com.myphotosanywhere.mpa.importing.TakeoutCodec.MAX_JSON_BYTES+1024) ?: "").also {
                    com.myphotosanywhere.mpa.importing.TakeoutCodec.validatePortable(it)
                })
        }
        require(assets.size<=10000 && assets.sumOf { it.faces.size }<=50000)
        require(people.map { it.id }.distinct().size==people.size && albums.map { it.id }.distinct().size==albums.size)
        require(assets.map { it.id }.distinct().size==assets.size && assets.map { it.remoteId }.distinct().size==assets.size)
        val faceIds=assets.flatMap { it.faces }.map { it.id }; require(faceIds.distinct().size==faceIds.size)
        require(assets.all { a -> a.faces.all { f -> f.personId==null || people.any { it.id==f.personId } } && a.albums.all { member -> albums.any { it.id==member } } })
        return XmpCatalog(root.required("account"),root.required("folder"),assets,people,albums)
    }
}
