package com.myphotosanywhere.mpa.importing

import com.google.gson.JsonParseException

internal data class TakeoutPreviewInput(val id: String,val raw: String?)
internal data class TakeoutPreviewMetadata(val inputs: List<TakeoutPreviewInput>,
    val records: Map<String,TakeoutMetadata>,val invalidIds: Set<String>)

/** Read-only inspection. A malformed provider record is explicit; internal failures propagate. */
internal class TakeoutPreviewAnalyzer {
    private val codec=TakeoutCodec()
    fun analyze(inputs: List<TakeoutPreviewInput>,checkpoint: ()->Unit = {}): TakeoutPreviewMetadata {
        require(inputs.map { it.id }.toSet().size==inputs.size) { "Duplicate import preview identities." }
        val records=linkedMapOf<String,TakeoutMetadata>();val invalid=mutableSetOf<String>()
        for(input in inputs) {
            checkpoint()
            val raw=input.raw ?: continue
            try { records[input.id]=codec.parse(raw) }
            catch(error: RuntimeException) {
                if(error !is JsonParseException && error !is IllegalArgumentException && error !is IllegalStateException &&
                    error !is UnsupportedOperationException && error !is ArithmeticException) throw error
                invalid.add(input.id)
            }
        }
        checkpoint()
        return TakeoutPreviewMetadata(inputs,records,invalid)
    }
}

internal enum class TakeoutPreviewFilter(val title: String) {
    ALL("All items"),PEOPLE("People labels"),MISSING("Without metadata"),REVIEW("Needs review")
}

/** Selection and transfer status remain owned by the import job, never by a preview filter. */
internal class TakeoutPreviewSnapshot(val entries: List<ImportItem>,val metadata: TakeoutPreviewMetadata) {
    private val media=entries.filter(::isMedia)
    init {
        require(entries.map { it.id }.toSet().size==entries.size) { "Duplicate import preview identities." }
        require(metadata.inputs==inputs(entries)) { "Stale import preview metadata." }
    }
    val mediaCount get()=media.size
    val withMetadata get()=metadata.records.size
    val withoutMetadata get()=media.count { it.metadata==null }
    val invalidMetadata get()=metadata.invalidIds.size
    val withPeople get()=media.count { names(it).isNotEmpty() }
    val differentNames get()=media.flatMap(::names).toSet().size
    val selectedMedia get()=media.count { it.selected }
    val selectedWithPeople get()=media.count { it.selected && names(it).isNotEmpty() }
    val reviewCount get()=entries.count(::needsReview)
    fun visible(filter: TakeoutPreviewFilter)=entries.filter { entry -> when(filter) {
        TakeoutPreviewFilter.ALL -> true
        TakeoutPreviewFilter.PEOPLE -> names(entry).isNotEmpty()
        TakeoutPreviewFilter.MISSING -> isMedia(entry) && entry.metadata==null
        TakeoutPreviewFilter.REVIEW -> needsReview(entry)
    } }
    private fun names(entry: ImportItem)=metadata.records[entry.id]?.people.orEmpty().map(String::trim).filter(String::isNotBlank).distinct()
    private fun needsReview(entry: ImportItem)=entry.id in metadata.invalidIds || entry.error!=null || entry.state in setOf("UNRESOLVED","UNSUPPORTED","FAILED")
    companion object {
        fun isMedia(entry: ImportItem)=entry.mime.startsWith("image/") || entry.mime.startsWith("video/")
        fun inputs(entries: List<ImportItem>)=entries.filter(::isMedia).map { TakeoutPreviewInput(it.id,it.metadata) }
    }
}
