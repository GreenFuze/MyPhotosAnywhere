package com.myphotosanywhere.mpa.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.listSaver

enum class ShellDestination(val id: Int, val title: String) {
    PHOTOS(0,"Photos"), COLLECTIONS(1,"Collections"), PEOPLE(2,"People"),
    PLACES(3,"Places"), STORAGE(4,"Storage & backup"), SEARCH(5,"Search"), AI_ACTIVITY(6,"AI activity");
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
    val canGoBack: Boolean get()=history.size>1 || destination!=ShellDestination.PHOTOS

    fun select(destination: ShellDestination) {
        require(destination.primary) { "Expected a primary destination" }
        history=listOf(destination)
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
