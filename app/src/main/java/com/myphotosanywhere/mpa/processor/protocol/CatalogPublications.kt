package com.myphotosanywhere.mpa.processor.protocol

import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.util.UUID
import java.security.MessageDigest

/** Selected locally/enrolled; a received reference cannot select its own workspace. */
data class CatalogWorkspace(val accountId: String, val folderId: String) {
    init { CatalogPublicationReader.checkOpaque(accountId); CatalogPublicationReader.checkOpaque(folderId) }
}
data class CatalogObjectReference(val accountId: String, val fileId: String, val workspaceId: String, val bytes: Long, val sha256: String)
data class CatalogChunkReference(val encoded: CatalogObjectReference, val expandedBytes: Long, val expandedSha256: String)
data class CatalogCommit(val generationId: UUID, val parent: CatalogObjectReference?, val rootIndex: CatalogObjectReference,
    val sourceCoverage: CatalogObjectReference, val settingsRevision: Long, val minimumHistoryGeneration: Long)
data class CatalogIndexChild(val edge: String, val reference: CatalogObjectReference, val chunk: CatalogChunkReference?)
data class CatalogIndexNode(val prefix: String, val children: List<CatalogIndexChild>)

/** Schemas only: verify signature/approved actor first. Drive membership, reference hashes,
 * ancestry and complete-tree validation remain mandatory before activating any generation. */
class CatalogPublicationReader(private val workspace: CatalogWorkspace) {
    fun readCommit(verified: VerifiedProtocolDocument): CatalogCommit {
        if (verified.kind != "catalog-commit" || CAPABILITY !in verified.requiredCapabilities)
            throw ProcessorProtocolException("A verified catalog commit with its required capability is required.")
        val root = canonical(verified.canonicalPayload.toByteArray(Charsets.UTF_8))
        val generation = uuid(root, "generationId")
        val parentValue = property(root, "parent")
        val parent = if (parentValue.isJsonNull) null else reference(parentValue, MAX_INDEX_BYTES)
        if ((verified.sequence == 1L) != (parent == null)) throw ProcessorProtocolException("Only the first stream commit may omit its predecessor.")
        val index = reference(property(root, "rootIndex"), MAX_INDEX_BYTES)
        val coverage = reference(property(root, "sourceCoverage"), MAX_INDEX_BYTES)
        val settings = decimal(root, "settingsRevision", allowZero = true)
        val minimum = decimal(root, "minimumHistoryGeneration")
        if (minimum > verified.sequence) throw ProcessorProtocolException("Required history exceeds the current stream sequence.")
        val ids = hashSetOf(index.fileId, coverage.fileId)
        if (ids.size != 2 || parent != null && !ids.add(parent.fileId)) throw ProcessorProtocolException("Distinct catalog roles cannot alias one Drive object.")
        return CatalogCommit(generation, parent, index, coverage, settings, minimum)
    }

    fun readIndex(canonicalBytes: ByteArray, expectedPrefix: String): CatalogIndexNode {
        if (expectedPrefix.length > 6 || expectedPrefix.length % 2 != 0 || !expectedPrefix.all(::isHex))
            throw ProcessorProtocolException("Invalid catalog index prefix.")
        val root = canonical(canonicalBytes)
        val majorValue = property(root, "major")
        if (!majorValue.isJsonPrimitive || !majorValue.asJsonPrimitive.isNumber)
            throw ProcessorProtocolException("Catalog major must be an integer.")
        val major = majorValue.asString.toIntOrNull() ?: throw ProcessorProtocolException("Catalog major must be an integer.")
        if (text(root, "format") != "mpa-catalog-index" || major != 1)
            throw ProtocolCompatibilityException("This catalog index requires a compatible MPA update.")
        if (text(root, "prefix") != expectedPrefix) throw ProcessorProtocolException("Catalog identity partition does not match its parent.")
        val children = property(root, "children")
        if (!children.isJsonArray || children.asJsonArray.size() > 256 || children.asJsonArray.size() == 0 && expectedPrefix.isNotEmpty())
            throw ProcessorProtocolException("Invalid bounded catalog index children.")
        val entries = ArrayList<CatalogIndexChild>(); val ids = HashSet<String>(); var previous: String? = null
        children.asJsonArray.forEach { child ->
            val edge = text(child, "edge")
            if (edge.length != 2 || !edge.all(::isHex) || previous?.let { it >= edge } == true)
                throw ProcessorProtocolException("Catalog edges must be unique sorted hexadecimal bytes.")
            previous = edge
            val kind = text(child, "kind")
            if (kind !in setOf("index", "chunk") || kind == "index" && expectedPrefix.length >= 6)
                throw ProcessorProtocolException("Catalog index exceeds its four partition levels.")
            val ref = reference(property(child, "object"), if (kind == "chunk") MAX_COMPRESSED_BYTES else MAX_INDEX_BYTES)
            if (!ids.add(ref.fileId)) throw ProcessorProtocolException("Catalog children cannot share one Drive object.")
            val expanded = property(child, "expanded")
            val chunk = if (kind == "chunk") {
                val bytes = decimal(expanded, "bytes"); val hash = digest(expanded, "sha256")
                if (bytes > MAX_EXPANDED_BYTES) throw ProcessorProtocolException("Expanded catalog chunk exceeds its bound.")
                CatalogChunkReference(ref, bytes, hash)
            } else {
                if (!expanded.isJsonNull) throw ProcessorProtocolException("Only chunks declare expanded bytes.")
                null
            }
            entries.add(CatalogIndexChild(edge, ref, chunk))
        }
        return CatalogIndexNode(expectedPrefix, entries.toList())
    }

    fun readCoverageRoot(canonicalBytes: ByteArray,library: UUID): CatalogCoverageRoot {
        require(library!=UUID(0,0)) { "An established library is required." }
        val root=canonical(canonicalBytes);coverageHeader(root,"mpa-source-coverage-root",library)
        val total=decimal(root,"sourceCount",allowZero=true);val rows=property(root,"pages")
        if(total>256 || !rows.isJsonArray || rows.asJsonArray.size()>16)
            throw ProcessorProtocolException("Source coverage exceeds its bounded source/page count.")
        val pages=mutableListOf<CatalogCoveragePageReference>();val ids=mutableSetOf<String>();var previous: UUID?=null;var count=0
        for(row in rows.asJsonArray) {
            val first=uuid(row,"firstId");val last=uuid(row,"lastId");val size=decimal(row,"count")
            val ref=reference(property(row,"reference"),MAX_INDEX_BYTES)
            if(size>16 || first.toString()>last.toString() || (size==1L)!=(first==last) ||
                previous?.let { it.toString()>=first.toString() }==true || !ids.add(ref.fileId))
                throw ProcessorProtocolException("Source coverage pages overlap, alias or exceed their range/count bounds.")
            pages.add(CatalogCoveragePageReference(first,last,size.toInt(),ref));count+=size.toInt();previous=last
        }
        if(count.toLong()!=total) throw ProcessorProtocolException("Coverage pages do not account for the declared source count.")
        return CatalogCoverageRoot(library,decimal(root,"configurationRevision"),decimal(root,"discoveryRevision"),count,pages.toList())
    }
    fun readCoveragePage(canonicalBytes: ByteArray,root: CatalogCoverageRoot,descriptor: CatalogCoveragePageReference): CatalogCoveragePage {
        if(descriptor !in root.pages || descriptor.reference.accountId!=workspace.accountId || descriptor.reference.workspaceId!=workspace.folderId)
            throw ProcessorProtocolException("Source page was not captured by this root and selected workspace.")
        if(canonicalBytes.size>MAX_INDEX_BYTES || canonicalBytes.size.toLong()!=descriptor.reference.bytes ||
            MessageDigest.getInstance("SHA-256").digest(canonicalBytes).joinToString("") { "%02x".format(it) }!=descriptor.reference.sha256)
            throw ProcessorProtocolException("Source page does not match its captured immutable bytes.")
        val value=canonical(canonicalBytes);coverageHeader(value,"mpa-source-coverage-page",root.libraryId)
        if(decimal(value,"configurationRevision")!=root.configurationRevision || decimal(value,"discoveryRevision")!=root.discoveryRevision)
            throw ProcessorProtocolException("Source page revisions do not match the captured root.")
        val rows=property(value,"sources")
        if(!rows.isJsonArray || rows.asJsonArray.size() !in 1..16 || rows.asJsonArray.size()!=descriptor.count)
            throw ProcessorProtocolException("Source page does not match its captured count.")
        val sources=mutableListOf<CatalogSourceCoverage>();val bindings=mutableSetOf<Pair<String,String>>();var previous: UUID?=null
        for(row in rows.asJsonArray) {
            val id=uuid(row,"id");val account=text(row,"accountId");val folder=text(row,"folderId")
            checkOpaque(account);checkOpaque(folder)
            if(folder=="root" || !bindings.add(account to folder) || previous?.let { it.toString()>=id.toString() }==true)
                throw ProcessorProtocolException("Source coverage requires unique ordered identities and canonical bindings.")
            val revision=decimal(row,"revision");val accountRevision=decimal(row,"accountRevision")
            val descendants=coverageFlag(row,"includeDescendants");val enabled=coverageFlag(row,"enabled");val connected=coverageFlag(row,"connected")
            val state=text(row,"state")
            if(state !in COVERAGE_STATES || !enabled && state!="disabled" || enabled && !connected && state!="disconnected" ||
                enabled && connected && (state=="disabled" || state=="disconnected"))
                throw ProcessorProtocolException("Coverage state contradicts captured policy or account availability.")
            val checkpointValue=property(row,"checkpoint")
            val checkpoint=if(checkpointValue.isJsonNull) null else CatalogScanCheckpoint(uuid(checkpointValue,"jobId"),
                decimal(checkpointValue,"sourceRevision"),decimal(checkpointValue,"accountRevision"),decimal(checkpointValue,"observedItems",true),
                decimal(checkpointValue,"pendingItems",true),decimal(checkpointValue,"failedItems",true),coverageFlag(checkpointValue,"incompleteSearch"),
                digest(checkpointValue,"sha256")).also {
                    if(it.sourceRevision>revision || it.accountRevision>accountRevision)
                        throw ProcessorProtocolException("Checkpoint comes from a future source/account revision.")
                }
            if((state=="complete" || state=="scanning" || state=="partial") && checkpoint==null)
                throw ProcessorProtocolException("Observed scan states require a captured checkpoint.")
            if(state=="complete" && (checkpoint!!.sourceRevision!=revision || checkpoint.accountRevision!=accountRevision ||
                checkpoint.pendingItems!=0L || checkpoint.failedItems!=0L || checkpoint.incompleteSearch))
                throw ProcessorProtocolException("An incomplete or stale scan cannot claim complete source coverage.")
            if(state=="partial" && checkpoint!!.pendingItems==0L && checkpoint.failedItems==0L && !checkpoint.incompleteSearch)
                throw ProcessorProtocolException("Partial coverage requires outstanding work or incomplete observations.")
            sources.add(CatalogSourceCoverage(id,account,folder,revision,accountRevision,descendants,enabled,connected,state,checkpoint));previous=id
        }
        if(sources.first().id!=descriptor.firstId || sources.last().id!=descriptor.lastId)
            throw ProcessorProtocolException("Source page does not match its captured identity range.")
        return CatalogCoveragePage(descriptor,sources.toList())
    }
    private fun coverageHeader(root: JsonElement,format: String,library: UUID) {
        val major=property(root,"major")
        if(text(root,"format")!=format || !major.isJsonPrimitive || !major.asJsonPrimitive.isNumber || major.toString()!="1")
            throw ProtocolCompatibilityException("Source coverage requires a compatible MPA update.")
        if(uuid(root,"libraryId")!=library) throw ProcessorProtocolException("Source coverage belongs to another library.")
    }
    private fun coverageFlag(root: JsonElement,name: String): Boolean {
        val value=property(root,name)
        if(!value.isJsonPrimitive || !value.asJsonPrimitive.isBoolean) throw ProcessorProtocolException("Coverage flag must be boolean: $name")
        return value.asBoolean
    }
    private fun reference(value: JsonElement, maximum: Int): CatalogObjectReference {
        val account = text(value, "accountId"); val file = text(value, "fileId"); val folder = text(value, "workspaceId")
        checkOpaque(account); checkOpaque(file); checkOpaque(folder)
        if (account != workspace.accountId || folder != workspace.folderId || file == folder)
            throw ProcessorProtocolException("Catalog reference is outside the selected metadata workspace.")
        val bytes = decimal(value, "bytes")
        if (bytes > maximum) throw ProcessorProtocolException("Catalog object exceeds its role's byte bound.")
        return CatalogObjectReference(account, file, folder, bytes, digest(value, "sha256"))
    }
    private fun canonical(bytes: ByteArray): JsonObject {
        val encoded = CanonicalProtocolJson(MAX_INDEX_BYTES).encode(bytes)
        if (!bytes.contentEquals(encoded)) throw ProcessorProtocolException("Catalog documents must already use canonical JSON bytes.")
        val element = JsonParser.parseString(encoded.toString(Charsets.UTF_8))
        if (!element.isJsonObject) throw ProcessorProtocolException("Catalog document must be an object.")
        return element.asJsonObject
    }
    private fun property(root: JsonElement, name: String): JsonElement =
        if (root.isJsonObject) root.asJsonObject[name] ?: throw ProcessorProtocolException("Missing catalog field: $name")
        else throw ProcessorProtocolException("Catalog field owner must be an object.")
    private fun text(root: JsonElement, name: String): String {
        val value = property(root, name)
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProcessorProtocolException("Catalog field must be a string: $name")
        return value.asString
    }
    private fun decimal(root: JsonElement, name: String, allowZero: Boolean = false): Long {
        val value = text(root, name)
        if (value.length !in 1..19 || value.length > 1 && value[0] == '0' || !value.all { it in '0'..'9' })
            throw ProcessorProtocolException("Invalid canonical catalog counter: $name")
        val number = value.toLongOrNull() ?: throw ProcessorProtocolException("Catalog counter overflow: $name")
        if (number < (if (allowZero) 0 else 1)) throw ProcessorProtocolException("Catalog counter is outside its range: $name")
        return number
    }
    private fun digest(root: JsonElement, name: String): String = text(root, name).also {
        if (it.length != 64 || !it.all(::isHex)) throw ProcessorProtocolException("Catalog digest must be lowercase SHA-256.")
    }
    private fun uuid(root: JsonElement, name: String): UUID {
        val value = text(root, name)
        if (!UUID_PATTERN.matches(value)) throw ProcessorProtocolException("Invalid catalog generation identity.")
        return UUID.fromString(value).also { if (it == UUID(0, 0)) throw ProcessorProtocolException("Zero generation identity is invalid.") }
    }
    companion object {
        const val CAPABILITY = "catalog.generations.v1"
        const val MAX_INDEX_BYTES = 256 * 1024
        const val MAX_COMPRESSED_BYTES = 4 * 1024 * 1024
        const val MAX_EXPANDED_BYTES = 16 * 1024 * 1024
        private val COVERAGE_STATES=setOf("complete","scanning","partial","pending","access-lost","offline","disconnected","disabled")
        private val UUID_PATTERN = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
        internal fun checkOpaque(value: String) {
            if (value.length !in 1..256 || !value.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '-' || it == '_' })
                throw ProcessorProtocolException("Catalog references require opaque Drive identities, never paths or URLs.")
        }
        private fun isHex(value: Char) = value in '0'..'9' || value in 'a'..'f'
    }
}
