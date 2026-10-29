package com.myphotosanywhere.mpa.processor.protocol

import java.util.Collections
import java.util.UUID

data class CatalogApprovedSource(val id: UUID, val accountId: String, val folderId: String, val revision: Long,
    val includeDescendants: Boolean, val enabled: Boolean)

/** The owner verifies an enrolled controller signature and selects this revision first.
 * Observed account connectivity cannot add, retarget or enable a source. This source
 * policy does not grant recognition consent or original-modification permission. */
class CatalogSourceConfiguration internal constructor(val libraryId: UUID, val revision: Long, val workspace: CatalogWorkspace,
    accounts: List<String>, sources: List<CatalogApprovedSource>) {
    val accounts: List<String> = Collections.unmodifiableList(accounts.toList())
    val sources: List<CatalogApprovedSource> = Collections.unmodifiableList(sources.toList())
    private val byId = sources.associateBy { it.id }
    fun validateCoverage(coverage: CatalogSourceCoverageSet) {
        if (coverage.root.libraryId != libraryId || coverage.root.configurationRevision != revision || coverage.sources.size != byId.size)
            throw ProcessorProtocolException("Captured coverage does not match the enrolled approved source configuration.")
        for (source in coverage.sources) {
            val approved = byId[source.id] ?: throw ProcessorProtocolException("Coverage names an unapproved source.")
            if (source.accountId != approved.accountId || source.folderId != approved.folderId || source.revision != approved.revision ||
                source.includeDescendants != approved.includeDescendants || source.enabled != approved.enabled)
                throw ProcessorProtocolException("Captured coverage changed an approved source binding, policy or revision.")
        }
    }
    companion object { const val CAPABILITY = "library.sources.v1" }
}
