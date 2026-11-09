package com.myphotosanywhere.mpa.processor.protocol

import java.util.UUID

data class CatalogCoveragePageReference(val firstId: UUID,val lastId: UUID,val count: Int,val reference: CatalogObjectReference)
data class CatalogCoverageRoot(val libraryId: UUID,val configurationRevision: Long,val discoveryRevision: Long,val sourceCount: Int,
    val pages: List<CatalogCoveragePageReference>)
data class CatalogScanCheckpoint(val jobId: UUID,val sourceRevision: Long,val accountRevision: Long,val observedItems: Long,
    val pendingItems: Long,val failedItems: Long,val incompleteSearch: Boolean,val sha256: String)
data class CatalogSourceCoverage(val id: UUID,val accountId: String,val folderId: String,val revision: Long,val accountRevision: Long,
    val includeDescendants: Boolean,val enabled: Boolean,val connected: Boolean,val state: String,val checkpoint: CatalogScanCheckpoint?)
data class CatalogCoveragePage(val descriptor: CatalogCoveragePageReference,val sources: List<CatalogSourceCoverage>)

/** Bounded complete mapping, including honestly partial/offline sources. Hash/Drive scope,
 * signed ancestry/authority, approved-configuration agreement and transactional activation
 * remain generation-owner checks. */
class CatalogSourceCoverageSet(val root: CatalogCoverageRoot,pages: Iterable<CatalogCoveragePage>) {
    private val byId=mutableMapOf<UUID,CatalogSourceCoverage>()
    val sources: List<CatalogSourceCoverage>
    init {
        if(root.sourceCount !in 0..256 || root.pages.size>16) throw ProcessorProtocolException("Coverage root exceeds its source/page count.")
        val seen=mutableSetOf<String>();val bindings=mutableSetOf<Pair<String,String>>()
        for(page in pages) {
            if(page.descriptor !in root.pages || !seen.add(page.descriptor.reference.fileId) || seen.size>16)
                throw ProcessorProtocolException("Coverage pages are duplicated or outside the captured root.")
            if(page.sources.size !in 1..16 || page.sources.size!=page.descriptor.count ||
                page.sources.first().id!=page.descriptor.firstId || page.sources.last().id!=page.descriptor.lastId)
                throw ProcessorProtocolException("Coverage page does not preserve its validated count/range.")
            for(source in page.sources) {
                if(byId.putIfAbsent(source.id,source)!=null || !bindings.add(source.accountId to source.folderId) || byId.size>256)
                    throw ProcessorProtocolException("Source coverage repeats an identity/binding or exceeds its bound.")
            }
        }
        if(seen.size!=root.pages.size || byId.size!=root.sourceCount)
            throw ProcessorProtocolException("All source coverage pages must validate before accepting facts.")
        sources = java.util.Collections.unmodifiableList(byId.values.sortedBy { it.id.toString() })
    }
    fun validateAsset(asset: DriveCatalogAsset) {
        if(asset.sources.isEmpty()) throw ProcessorProtocolException("Asset requires a captured source membership.")
        for(membership in asset.sources) {
            val source=byId[membership.id] ?: throw ProcessorProtocolException("Unknown asset source membership.")
            if(source.accountId!=asset.accountId || source.revision!=membership.revision || !source.enabled ||
                membership.depth !in 0..256 || !source.includeDescendants && membership.depth!=0)
                throw ProcessorProtocolException("Asset membership contradicts its captured source coverage.")
        }
    }
}
