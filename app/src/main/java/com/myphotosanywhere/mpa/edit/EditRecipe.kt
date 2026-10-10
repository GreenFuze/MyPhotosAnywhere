package com.myphotosanywhere.mpa.edit

import com.google.gson.Gson

data class Stroke(val points: List<MarkPoint>, val color: Int = 0xFFFF5252.toInt(), val width: Float = .012f)
data class MarkPoint(val x: Float, val y: Float)
/** Crop coordinates refer to the upright source, markup to the final output. */
data class EditRecipe(val left: Float = 0f, val top: Float = 0f, val right: Float = 1f, val bottom: Float = 1f,
    val quarterTurns: Int = 0, val straighten: Float = 0f, val exposure: Float = 0f,
    val contrast: Float = 1f, val saturation: Float = 1f, val warmth: Float = 0f,
    val filter: String = "Original", val strokes: List<Stroke> = emptyList(), val keepLocation: Boolean = false) {
    fun validate(): EditRecipe {
        require(listOf(left,top,right,bottom,straighten,exposure,contrast,saturation,warmth).all { it.isFinite() })
        require(left >= 0 && top >= 0 && right <= 1 && bottom <= 1 && right-left >= .05f && bottom-top >= .05f)
        require(quarterTurns in 0..3 && straighten in -15f..15f && exposure in -2f..2f && contrast in .5f..2f &&
            saturation in 0f..2f && warmth in -1f..1f && filter in FILTERS)
        require(strokes.size <= 100 && strokes.all { it.width in .002f.. .05f && it.points.size <= 4000 &&
            it.points.all { p -> p.x.isFinite() && p.y.isFinite() && p.x in 0f..1f && p.y in 0f..1f } })
        return this
    }
    fun json(): String = Gson().toJson(validate())
    companion object {
        val FILTERS = listOf("Original", "Mono", "Sepia", "Vivid", "Cool")
        fun parse(json: String): EditRecipe = Gson().fromJson(json, EditRecipe::class.java).validate()
    }
}
class EditHistory(initial: EditRecipe) {
    private val undo = ArrayDeque<EditRecipe>()
    private val redo = ArrayDeque<EditRecipe>()
    var current: EditRecipe = initial.validate(); private set
    val canUndo get() = undo.isNotEmpty()
    val canRedo get() = redo.isNotEmpty()
    fun commit(recipe: EditRecipe) {
        recipe.validate(); if (recipe == current) return
        undo.addLast(current); if (undo.size > 50) undo.removeFirst()
        current = recipe; redo.clear()
    }
    fun undo(): EditRecipe { if (canUndo) { redo.addLast(current); current = undo.removeLast() }; return current }
    fun redo(): EditRecipe { if (canRedo) { undo.addLast(current); current = redo.removeLast() }; return current }
}
