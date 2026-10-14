package com.myphotosanywhere.mpa.importing

/** Keep durable failure reasons; only known legacy navigation instructions need presentation repair. */
internal object ImportStatusText {
    fun inApp(detail: String): String = detail
        .replace("Open MPA and resume", "Resume this import")
        .replace("Open MPA to resume", "Resume this import")
        .replace("Reopen MPA to continue", "Resume this import to continue")
        .replace("Open MPA to continue", "Resume this import to continue")

    fun notification(progress: ImportProgress): String {
        val detail = inApp(progress.detail)
        return if (progress.state in setOf("FAILED", "PAUSED", "WAITING_FOR_APP"))
            "$detail · Open MPA to review this import" else detail
    }
}
