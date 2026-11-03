package com.myphotosanywhere.mpa.processor.protocol

import java.util.UUID

/** Select the current controller sequence and separately admit source scope and ancestry.
 * This policy never grants recognition consent or permission to modify originals. */
class CatalogPublisherAuthorization internal constructor(
    val libraryId: UUID,
    val controllerId: UUID,
    val controllerEpoch: Long,
    val sequence: Long,
    val workspace: CatalogWorkspace,
    val publisher: TrustedProtocolActor?,
    val publisherKeyId: String?,
    val envelopeSha256: String,
) {
    companion object { const val CAPABILITY = "library.publishers.v1" }
}
