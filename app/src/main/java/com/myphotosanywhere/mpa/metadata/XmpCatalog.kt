package com.myphotosanywhere.mpa.metadata

import com.myphotosanywhere.mpa.people.FaceRegion
import com.myphotosanywhere.mpa.edit.EditRecipe
import java.io.StringReader
import java.io.OutputStreamWriter
import javax.xml.parsers.DocumentBuilderFactory
import javax.xml.transform.TransformerFactory
import javax.xml.transform.TransformerException
import javax.xml.transform.dom.DOMSource
import javax.xml.transform.stream.StreamResult
import org.w3c.dom.Element
import org.xml.sax.InputSource

data class PortablePerson(val id: String,val name: String)
data class PortableAlbum(val id: String,val name: String)
data class PortableAlias(val key: String,val name: String,val binding: String?,val personId: String?,val ignored: Boolean)
data class PortableMembership(val aliasKey: String,val personId: String,val excluded: Boolean)
data class PortablePhotoPeople(val origin: String?,val members: List<PortableMembership>)
data class PortableFace(val id: String,val personId: String?,val corrected: Boolean,val region: FaceRegion?,
    val width: Int?,val height: Int?,val model: String?)
data class PortableAsset(val id: String,val remoteId: String,val sha256: String,val bytes: Long,
    val favorite: Boolean,val archived: Boolean,val place: String,val latitude: Double?,val longitude: Double?,
    val capturedAt: Long?,val parentId: String?,val provider: String?,val instructions: String?,val labels: String,
    val recipe: String?,val albums: List<String>,val faces: List<PortableFace>,val importedMetadata: String = "",
    val photoPeople: PortablePhotoPeople?=null)
data class XmpCatalog(val accountId: String,val folderId: String,val assets: List<PortableAsset>,
    val people: List<PortablePerson>,val albums: List<PortableAlbum>,val aliases: List<PortableAlias> = emptyList())

/** MPA RDF extension. Unknown namespaces are retained; embeddings, tokens and paths are never exported. */
object XmpCodec {
    const val NS="https://myphotosanywhere.app/ns/1.0/"
    private const val RDF="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
    const val MAX_BYTES=2*1024*1024
    private fun document(xml: String) = DocumentBuilderFactory.newInstance().apply {
        isNamespaceAware=true; isExpandEntityReferences=false
        // Android's parser does not implement every JAXP feature. Reject declarations before parsing too.
        require(!Regex("<!\\s*(DOCTYPE|ENTITY)",RegexOption.IGNORE_CASE).containsMatchIn(xml)) { "XMP document declarations are not supported." }
        XmpSizeBudget().add(xml)
        runCatching { setFeature("http://apache.org/xml/features/disallow-doctype-decl",true) }
        runCatching { setFeature("http://xml.org/sax/features/external-general-entities",false) }
        runCatching { setFeature("http://xml.org/sax/features/external-parameter-entities",false) }
    }.newDocumentBuilder().parse(InputSource(StringReader(xml)))
    fun encode(catalog: XmpCatalog,previous: String?=null): String {
        // Count the minimum wire size before allocating any catalog DOM nodes.
        // The final bounded stream also accounts for escaping and retained foreign XML.
        val budget=XmpSizeBudget()
        visitCatalog(catalog,{ name,attrs ->
            budget.add("<mpa:$name/>")
            attrs.forEach { (key,value) -> if(value!=null) {
                budget.add(" $key=\"\"");budget.add(value.toString())
            } }
        },{})
        val doc=document(previous ?: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"$RDF\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>")
        val descriptions=doc.getElementsByTagNameNS(RDF,"Description")
        require(descriptions.length>0) { "Missing XMP RDF description." }
        val old=doc.getElementsByTagNameNS(NS,"Catalog")
        // Only our catalog is replaced. Lightroom/MWG and other external fields are preserved.
        while(old.length>0) old.item(0).parentNode.removeChild(old.item(0))
        var parent=descriptions.item(0)
        visitCatalog(catalog,{ name,attrs ->
            val element=doc.createElementNS(NS,"mpa:$name")
            attrs.forEach { (key,value) -> if(value!=null) element.setAttribute(key,value.toString()) }
            if(name=="Catalog") element.setAttributeNS("http://www.w3.org/2000/xmlns/","xmlns:mpa",NS)
            parent.appendChild(element);parent=element
        },{ parent=parent.parentNode })
        val output=BoundedXmpOutput()
        try {
            OutputStreamWriter(output,Charsets.UTF_8).use { writer ->
                TransformerFactory.newInstance().newTransformer().transform(DOMSource(doc),StreamResult(writer))
            }
        } catch(error: TransformerException) {
            generateSequence<Throwable>(error) { it.cause }.filterIsInstance<XmpSizeLimitException>().firstOrNull()?.let { throw it }
            throw error
        }
        return output.toString(Charsets.UTF_8.name()).also { decode(it) }
    }
    /** Both passes enumerate exactly the same attributes; neither retains a second catalog. */
    private fun visitCatalog(catalog: XmpCatalog,start: (String,Map<String,Any?>)->Unit,end: ()->Unit) {
        fun leaf(name: String,attrs: Map<String,Any?>) { start(name,attrs);end() }
        start("Catalog",mapOf("schema" to 1,"account" to catalog.accountId,"folder" to catalog.folderId))
        catalog.people.forEach { leaf("Person",mapOf("id" to it.id,"name" to it.name)) }
        catalog.aliases.forEach { leaf("PersonAlias",mapOf("key" to it.key,"name" to it.name,
            "binding" to it.binding,"person" to it.personId,"ignored" to it.ignored)) }
        catalog.albums.forEach { leaf("Album",mapOf("id" to it.id,"name" to it.name)) }
        catalog.assets.forEach { a ->
            start("Asset",mapOf("id" to a.id,"remote" to a.remoteId,"sha256" to a.sha256,"bytes" to a.bytes,
                "favorite" to a.favorite,"archived" to a.archived,"place" to a.place,"latitude" to a.latitude,
                "longitude" to a.longitude,"captured" to a.capturedAt,"parent" to a.parentId,"provider" to a.provider,
                "instructions" to a.instructions,"labels" to a.labels,"recipe" to a.recipe,"importedMetadata" to a.importedMetadata))
            a.albums.forEach { leaf("Member",mapOf("album" to it)) }
            a.faces.forEach { f -> leaf("Face",mapOf("id" to f.id,"person" to f.personId,
                "corrected" to f.corrected,"left" to f.region?.left,"top" to f.region?.top,"right" to f.region?.right,
                "bottom" to f.region?.bottom,"width" to f.width,"height" to f.height,"model" to f.model)) }
            a.photoPeople?.let { p ->
                start("PhotoPeople",mapOf("origin" to p.origin))
                p.members.forEach { leaf("Named",mapOf("alias" to it.aliasKey,"person" to it.personId,"excluded" to it.excluded)) }
                end()
            }
            end()
        }
        end()
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
        val people=root.children("Person").map { PortablePerson(it.required("id"),it.required("name",200)) }
        val aliases=root.children("PersonAlias").map { a ->
            PortableAlias(a.required("key",4096),a.required("name",200),a.text("binding"),a.text("person"),a.flag("ignored")).also {
                require(com.myphotosanywhere.mpa.people.ImportedPersonLabel(it.name,it.binding).key==it.key) { "Invalid provider alias identity." }
                require(it.ignored==(it.personId==null)) { "Invalid provider alias destination." }
            }
        }
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
            val named=a.children("PhotoPeople").also { require(it.size<=1) }.singleOrNull()?.let { p ->
                PortablePhotoPeople(p.text("origin"),p.children("Named").map { PortableMembership(it.required("alias",4096),it.required("person"),it.flag("excluded")) }).also {
                    require(it.members.map { m -> m.aliasKey }.distinct().size==it.members.size)
                }
            }
            PortableAsset(a.required("id"),a.required("remote"),a.required("sha256").also { require(it.matches(Regex("[a-f0-9]{64}"))) },
                a.required("bytes").toLong().also { require(it>0) },a.flag("favorite"),a.flag("archived"),a.text("place",500) ?: "",
                lat,lon,a.text("captured")?.toLong(),a.text("parent"),a.text("provider"),a.text("instructions",MAX_BYTES),
                a.text("labels",5000) ?: "",recipe,members,faces,(a.text("importedMetadata",com.myphotosanywhere.mpa.importing.TakeoutCodec.MAX_JSON_BYTES+1024) ?: "").also {
                    com.myphotosanywhere.mpa.importing.TakeoutCodec.validatePortable(it)
                },named)
        }
        require(assets.size<=10000 && assets.sumOf { it.faces.size }<=50000)
        require(people.map { it.id }.distinct().size==people.size && albums.map { it.id }.distinct().size==albums.size)
        require(assets.map { it.id }.distinct().size==assets.size && assets.map { it.remoteId }.distinct().size==assets.size)
        val faceIds=assets.flatMap { it.faces }.map { it.id }; require(faceIds.distinct().size==faceIds.size)
        require(assets.all { a -> a.faces.all { f -> f.personId==null || people.any { it.id==f.personId } } && a.albums.all { member -> albums.any { it.id==member } } })
        require(aliases.size<=50000 && aliases.map { it.key }.distinct().size==aliases.size)
        require(assets.sumOf { it.photoPeople?.members?.size ?: 0 }<=50000)
        val peopleIds=people.map { it.id }.toSet();val aliasesByKey=aliases.associateBy { it.key }
        require(aliases.all { it.ignored || it.personId in peopleIds }) { "Provider alias references an unavailable person." }
        require(assets.all { a -> a.photoPeople?.members?.all { m ->
            m.personId in peopleIds && aliasesByKey[m.aliasKey]?.let { !it.ignored && it.personId==m.personId }==true
        }!=false }) { "Photo association references an unavailable provider alias." }
        return XmpCatalog(root.required("account"),root.required("folder"),assets,people,albums,aliases)
    }
}
