package com.myphotosanywhere.mpa.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.listSaver

enum class ShellDestination(val id: Int, val title: String) {
    PHOTOS(0,"Photos"), COLLECTIONS(1,"Collections"), PEOPLE(2,"People"),
    PLACES(3,"Places"), STORAGE(4,"Sources"), SEARCH(5,"Search"), AI_ACTIVITY(6,"AI activity"),
    ACTIVITY(7,"Activity"), IMPORTS(8,"Import photos"), IMPORT_HISTORY(9,"Import history"),
    SETTINGS(10,"Settings"), CACHE(11,"Storage and cache"), RECOGNITION(12,"Recognition"),
    ANNOTATIONS(13,"Backup and metadata"), PRIVACY(14,"Privacy and permissions"), ABOUT(15,"About MPA"), TRASH(16,"Trash");
    val primary: Boolean get()=this in listOf(PHOTOS,COLLECTIONS,SEARCH)
}

/** Presentation history only: never changes media, sources, or captured import destinations. */
class ShellNavigation(initial: List<ShellDestination> = listOf(ShellDestination.PHOTOS)) {
    init {
        require(initial.isNotEmpty()) { "Navigation must have a destination" }
        require(initial.first().primary) { "Navigation must start at a primary destination" }
    }
    private var history by mutableStateOf(initial.toList())
    val destination: ShellDestination get()=history.last()
    val primary: ShellDestination get()=history.first()
    val activePrimary: ShellDestination get()=if(ShellDestination.SEARCH in history) ShellDestination.SEARCH else primary
    val canGoBack: Boolean get()=history.size>1 || destination!=ShellDestination.PHOTOS

    fun select(destination: ShellDestination) {
        require(destination.primary) { "Expected a primary destination" }
        if(destination==ShellDestination.SEARCH) {
            if(this.destination!=destination) history=history+destination
        } else history=listOf(destination)
    }
    fun open(destination: ShellDestination) {
        if(this.destination!=destination) history=history+destination
    }
    fun back() {
        history=if(history.size>1) history.dropLast(1) else listOf(ShellDestination.PHOTOS)
    }
    companion object {
        val Saver=listSaver<ShellNavigation,Int>(
            save={ state -> state.history.map { it.id } },
            restore={ ids -> ShellNavigation(ids.map { id -> ShellDestination.entries.single { it.id==id } }) }
        )
    }
}
